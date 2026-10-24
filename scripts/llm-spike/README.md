# LLM spike harness

- **Purpose:** the throw-away tooling of the one-week feasibility spike of the LLM analysis (`docs/rfc/0002-llm-feasibility-spike.md`). It measures; it decides nothing, and nothing here becomes production code by being here.
- **Owner:** Eder · **Status:** draft · **Target phase:** 4

Python 3, standard library only. The tests use a fake Ollama server, so they need no model and no network:

```
cd scripts/llm-spike && python3 -m unittest test_spike
```

## The week, in commands
```
# day 2: the contexts (the demo ones only prove the harness works; they are invented)
python3 spike.py contexts --out work/contexts --demo
python3 spike.py contexts --out work/contexts --base-url http://127.0.0.1:8080 --token-env SAKER_TOKEN \
        --stocks XNAS:AAPL,XNYS:KO,... [--extras work/extras]     # extras/<MIC>_<TICKER>.json adds profile, filings and, for L2, an excerpt

# day 3, on the Oracle VM with the stack running (the figures of any other machine do not count)
python3 spike.py run --contexts work/contexts --out work/ollama-3b --model <model:tag> --base-url http://127.0.0.1:11434 --level L1 --poison
python3 spike.py run --contexts work/contexts --out work/ollama-3b --model <model:tag> --base-url http://127.0.0.1:11434 --level L2 --poison --constrained
# the external reference, the same contexts through an OpenAI-compatible endpoint (the key is read from the environment, never typed in a command)
python3 spike.py run --contexts work/contexts --out work/hosted --api openai --model <model> --base-url https://<endpoint>/v1 --key-env HOSTED_KEY

# days 4 and 5
python3 spike.py sheets --out work/ollama-3b        # one page per stock for the owner's review (about 3 hours for 20)
python3 spike.py report --out work/ollama-3b [--thresholds thresholds.json]
```

## What it records
Per call: tokens in and out, wall time and, with Ollama, the runtime's own prefill and generation times; whether the answer validated at the first try or after one repair; whether every number and every quoted fact is in the context; the memory the machine had available before and after (`/proc/meminfo`). Repeating a call (`--repeat`, default 2) shows whether the ratings are the same; `--poison` adds three contexts with an instruction in the company name (and in the excerpt at L2) and checks that the canary does not appear and the ratings do not change.

## What it does not do
No queue, no database, no scoring, no UI, no final prompt: `prompts/analysis.draft.md` and `analysis.schema.json` carry `TODO(owner: Eder, rfc-0002)` where the rubric is not written. The quality of the assessments is the owner's review of the sheets; the report only measures speed, format, grounding, stability and resistance. The thresholds in the report are the defaults of the RFC and can be replaced with `--thresholds`.
