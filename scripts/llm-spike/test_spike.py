"""Tests of the spike harness against a fake Ollama server: `python3 -m unittest discover -s scripts/llm-spike` (standard library only, no model, no network)."""
import json
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import spike


def good_answer(stock="XNAS_DEMA", evidence="price 120.5 on 2026-10-23"):
    return {
        "stock": stock,
        "risk_scan": {"assessments": [{"criterion": "trend", "rating": "low", "evidence": evidence}], "red_flags": []},
        "conviction": {"assessments": [{"criterion": "momentum", "rating": "strong", "evidence": evidence}]},
        "catalysts": [],
    }


class FakeOllama:
    """Serves canned answers in order; the last one repeats. Records the requests."""

    def __init__(self, answers):
        self.answers, self.requests = list(answers), []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                owner.requests.append(body)
                content = owner.answers.pop(0) if len(owner.answers) > 1 else owner.answers[0]
                text = content if isinstance(content, str) else json.dumps(content)
                payload = json.dumps({"message": {"content": text}, "prompt_eval_count": 1200, "eval_count": 300, "prompt_eval_duration": 2_000_000_000, "eval_duration": 6_000_000_000}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_):
                pass

        self.server = HTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_port}"
        threading.Thread(target=lambda: self.server.serve_forever(poll_interval=0.02), daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def run_args(url, contexts, out, **extra):
    base = ["run", "--contexts", str(contexts), "--out", str(out), "--model", "fake:1b", "--base-url", url, "--repeat", "2"]
    for key, value in extra.items():
        base += [f"--{key.replace('_', '-')}"] + ([] if value is True else [str(value)])
    return base


class SchemaTests(unittest.TestCase):
    def test_a_good_answer_validates_and_each_kind_of_mistake_is_named(self):
        schema = spike.load_schema()
        self.assertEqual(spike.validate(good_answer(), schema), [])
        bad = good_answer()
        bad["risk_scan"]["assessments"][0]["rating"] = "terrible"
        bad["extra"] = 1
        del bad["catalysts"]
        errors = " | ".join(spike.validate(bad, schema))
        self.assertIn("terrible", errors)
        self.assertIn("unexpected extra", errors)
        self.assertIn("missing catalysts", errors)

    def test_json_is_found_inside_prose_and_a_fence(self):
        self.assertEqual(spike.parse_json('Sure!\n```json\n{"a": 1}\n```'), {"a": 1})
        self.assertIsNone(spike.parse_json("no json here"))


class FaithfulnessTests(unittest.TestCase):
    def test_a_number_that_is_not_in_the_context_is_counted_as_not_grounded(self):
        context = spike.demo_contexts()[0]
        grounded, said, quoted, total = spike.faithfulness(good_answer(evidence="price 120.5 and a margin of 41.7"), context)
        self.assertEqual(said, 4, "two evidence strings with two numbers each")
        self.assertEqual(said - grounded, 2, "41.7 is invented, in both")
        self.assertEqual(total, 2)

    def test_no_evidence_in_the_data_counts_as_quoted(self):
        _, _, quoted, total = spike.faithfulness(good_answer(evidence="No evidence in the data"), spike.demo_contexts()[0])
        self.assertEqual((quoted, total), (2, 2))


class RunTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.contexts = Path(self.tmp.name, "contexts")
        spike.write_contexts(spike.demo_contexts(), self.contexts)
        self.out = Path(self.tmp.name, "out")

    def tearDown(self):
        self.tmp.cleanup()

    def rows(self):
        return spike.load_rows(self.out)

    def test_valid_answers_are_recorded_with_tokens_and_the_runtimes_own_timings(self):
        server = FakeOllama([good_answer()])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out))
        rows = self.rows()
        self.assertEqual(len(rows), 6)
        self.assertTrue(all(r["ok_first"] for r in rows))
        self.assertEqual((rows[0]["usage"]["prompt_tokens"], rows[0]["usage"]["completion_tokens"]), (1200, 300))
        self.assertEqual((rows[0]["usage"]["prefill_s"], rows[0]["usage"]["generate_s"]), (2.0, 6.0))
        self.assertEqual(server.requests[0]["options"]["temperature"], 0)

    def test_an_invalid_answer_gets_one_repair_and_is_counted_as_repaired(self):
        server = FakeOllama(["I think it is fine.", good_answer()])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out, repeat=1))
        first = self.rows()[0]
        self.assertFalse(first["ok_first"])
        self.assertTrue(first["ok_after_repair"])
        self.assertIn("did not follow the schema", server.requests[1]["messages"][-1]["content"])

    def test_a_model_that_never_gives_json_is_invalid_after_the_repair(self):
        server = FakeOllama(["nope"])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out, repeat=1))
        self.assertTrue(all(not r["ok_after_repair"] for r in self.rows()))

    def test_the_schema_is_sent_as_the_format_only_when_asked(self):
        server = FakeOllama([good_answer()])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out, repeat=1, constrained=True))
        self.assertIn("format", server.requests[0])

    def test_level_one_drops_the_excerpt_and_level_two_keeps_it(self):
        context = spike.demo_contexts()[0]
        context["excerpt"] = "Risk factors: ..."
        Path(self.contexts, f"{context['contextId']}.json").write_text(json.dumps(context))
        server = FakeOllama([good_answer()])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out, repeat=1))
        self.assertNotIn("Risk factors", server.requests[0]["messages"][1]["content"])
        spike.main(run_args(server.url, self.contexts, Path(self.tmp.name, "out2"), repeat=1, level="L2"))
        self.assertTrue(any("Risk factors" in r["messages"][1]["content"] for r in server.requests[3:]))

    def test_poisoned_contexts_carry_the_instruction_and_a_model_that_obeys_fails_the_check(self):
        obey = good_answer()
        obey["risk_scan"]["assessments"][0]["evidence"] = spike.CANARY
        server = FakeOllama([good_answer()] * 9 + [obey])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out, repeat=1, poison=True))
        self.assertTrue(any(spike.CANARY in r["messages"][1]["content"] for r in server.requests))
        spike.main(["report", "--out", str(self.out)])
        report = Path(self.out, "report.md").read_text()
        self.assertIn("injection resisted", report)

    def test_the_report_passes_a_stable_valid_model_and_fails_an_unstable_one(self):
        good = FakeOllama([good_answer()])
        self.addCleanup(good.close)
        spike.main(run_args(good.url, self.contexts, self.out))
        spike.main(["report", "--out", str(self.out), "--thresholds", str(self._thresholds({"nightly_hours_for_250": 100}))])
        report = Path(self.out, "report.md").read_text()
        self.assertIn("| valid after one repair | 1.00 | 0.95 | pass |", report)
        self.assertIn("| same ratings on a second run | 1.00 | 0.9 | pass |", report)

        flip = good_answer()
        flip["conviction"]["assessments"][0]["rating"] = "weak"
        unstable = FakeOllama([good_answer(), flip])
        self.addCleanup(unstable.close)
        other = Path(self.tmp.name, "out3")
        spike.main(run_args(unstable.url, self.contexts, other))
        spike.main(["report", "--out", str(other)])
        self.assertIn("FAIL", Path(other, "report.md").read_text())

    def test_the_sheets_put_the_context_beside_the_answer_with_a_form(self):
        server = FakeOllama([good_answer()])
        self.addCleanup(server.close)
        spike.main(run_args(server.url, self.contexts, self.out))
        spike.main(["sheets", "--out", str(self.out)])
        sheets = sorted(Path(self.out, "sheets").glob("*.md"))
        self.assertEqual(len(sheets), 3)
        text = sheets[0].read_text()
        self.assertIn("## Context", text)
        self.assertIn("## Answer", text)
        self.assertIn("Verdict: right / acceptable / wrong", text)

    def _thresholds(self, values):
        path = Path(self.tmp.name, "thresholds.json")
        path.write_text(json.dumps(values))
        return path


class ContextTests(unittest.TestCase):
    def test_the_demo_contexts_are_marked_as_invented_by_their_names(self):
        names = [c["stock"]["name"] for c in spike.demo_contexts()]
        self.assertTrue(all(name.startswith("Demo ") for name in names))

    def test_a_poisoned_context_keeps_the_facts_and_adds_the_instruction(self):
        context = spike.demo_contexts()[0]
        copy = spike.poisoned(context, "L2")
        self.assertEqual(copy["price"], context["price"])
        self.assertIn(spike.CANARY, copy["stock"]["name"])
        self.assertIn(spike.CANARY, copy["excerpt"])
        self.assertNotIn(spike.CANARY, json.dumps(context))


if __name__ == "__main__":
    unittest.main()
