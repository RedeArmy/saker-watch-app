// Load baseline of the Phase 2 read paths (milestone 2.8, ADR-0041). Run it with scripts/run-k6.sh, which builds the throwaway stack,
// seeds 500 listings with six years of prices, starts the API and mints the tokens; running it by hand needs the same environment:
//   K6_BASE_URL   the API, for example http://127.0.0.1:58090
//   K6_TOKENS     comma-separated access tokens, one per user
//   K6_ENFORCE    "1" to fail the run when a threshold is crossed (the budgets of ADR-0033); anything else only reports
//   K6_PHASE_SECONDS  seconds each scenario runs (default 30)
//   K6_WARMUP_SECONDS the first seconds of each scenario, tagged phase:warmup and left out of the thresholds and of the report (default 5): the
//                 first requests of a scenario meet cold code paths and plans, which says nothing about the steady latency the budget is about
//   K6_P95_BUDGET_MS  the budget of the p95 of every endpoint (default 300, ADR-0033); a slower machine can state its own
// The scenarios run one after the other, so each number is that endpoint alone on the machine and not a mix.
import exec from 'k6/execution';
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const base = __ENV.K6_BASE_URL;
const tokens = (__ENV.K6_TOKENS || '').split(',').filter((t) => t.length > 0);
const seconds = Number.parseInt(__ENV.K6_PHASE_SECONDS || '30', 10);
const enforce = __ENV.K6_ENFORCE === '1';
const warmupMs = Number.parseInt(__ENV.K6_WARMUP_SECONDS || '5', 10) * 1000;
const budgetMs = Number.parseInt(__ENV.K6_P95_BUDGET_MS || '300', 10);
const stocks = 500;
const portfolioStocks = 100;
const vus = Number.parseInt(__ENV.K6_VUS || '4', 10);

const scenarioNames = ['watchlist_by_ticker', 'watchlist_by_performance', 'sparklines', 'watchlist_page', 'portfolio_metrics', 'calculator'];

export const options = {
  setupTimeout: '10m',
  scenarios: Object.fromEntries(scenarioNames.map((name, index) => [name, {
    executor: 'constant-vus',
    exec: name,
    vus,
    duration: `${seconds}s`,
    startTime: `${index * (seconds + 5)}s`,
    tags: { endpoint: name },
  }])),
  // The budget of the whole watchlist is 300 ms (ADR-0030, ADR-0033); the others are held to the same figure. It is judged on the steady part of
  // each scenario. The threshold on the count (always true) is what makes k6 keep the sub-metric of each endpoint for the report.
  // The page scenario sends two requests at once; what a user waits for is the slower of them, so it is judged on page_ms and not on each request.
  thresholds: Object.fromEntries(scenarioNames.flatMap((name) => [
    [name === 'watchlist_page' ? `page_ms{endpoint:${name},phase:steady}` : `http_req_duration{endpoint:${name},phase:steady}`, enforce ? [`p(95)<${budgetMs}`] : []],
    [`http_req_failed{endpoint:${name},phase:steady}`, enforce ? ['rate<0.01'] : []],
    [`http_reqs{endpoint:${name},phase:steady}`, ['count>=0']],
  ])),
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const settled = new Trend('settled_ms');
const pageTime = new Trend('page_ms', true);

function headers(token, extra = {}) {
  return { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...extra } };
}

function ticker(n) {
  return `T${String(n).padStart(4, '0')}`;
}

function ok(response, what) {
  if (response.status >= 400) {
    throw new Error(`${what}: ${response.status} ${response.body}`);
  }
}

// Each user follows the 500 listings and has one portfolio with the first 100: the size the SLO is written for.
export function setup() {
  if (!base || tokens.length === 0) {
    throw new Error('K6_BASE_URL and K6_TOKENS are required');
  }

  const portfolios = tokens.map((token, index) => {
    ok(http.get(`${base}/v1/me`, headers(token)), 'me');
    for (let from = 1; from <= stocks; from += 25) {
      const requests = [];
      for (let n = from; n < from + 25 && n <= stocks; n++) {
        requests.push(['PUT', `${base}/v1/watchlist/items/XNAS/${ticker(n)}`, JSON.stringify({ notes: null, targetPrice: null }), headers(token)]);
      }

      http.batch(requests).forEach((response) => ok(response, 'put watchlist item'));
    }

    const created = http.post(
      `${base}/v1/portfolios`,
      JSON.stringify({ name: `Load ${index}`, description: null }),
      headers(token, { 'Idempotency-Key': `load-portfolio-${index}-${Date.now()}-0123456789` }));
    ok(created, 'create portfolio');
    const id = created.json('id');
    // One at a time: a portfolio serializes its changes on a lock (the order of its members), so a batch of parallel requests into it would only queue
    // behind each other, and a user fills a portfolio one stock after another.
    for (let n = 1; n <= portfolioStocks; n++) {
      ok(http.put(`${base}/v1/portfolios/${id}/items/XNAS/${ticker(n)}`, null, headers(token)), 'put portfolio item');
    }

    // A first read of everything, so the first measured request is not the one that fills a cache.
    ok(http.get(`${base}/v1/watchlist?sort=1y&order=desc&limit=50`, headers(token)), 'warm up');
    return id;
  });

  return { portfolios };
}

function pick(data) {
  const user = __VU % tokens.length;
  return { token: tokens[user], portfolio: data.portfolios[user] };
}

// The part of its scenario a request belongs to: the first seconds are the warm-up.
function phase() {
  return Date.now() - exec.scenario.startTime < warmupMs ? 'warmup' : 'steady';
}

function get(url, token, name) {
  const response = http.get(url, { ...headers(token), tags: { name, phase: phase() } });
  check(response, { [`${name} answered 200`]: (r) => r.status === 200 });
  settled.add(response.timings.duration);
  return response;
}

export function watchlist_by_ticker(data) {
  get(`${base}/v1/watchlist?limit=50`, pick(data).token, 'GET /v1/watchlist');
}

// The costly one: sorting by a window needs the numbers of all 500 stocks (ADR-0033).
export function watchlist_by_performance(data) {
  get(`${base}/v1/watchlist?sort=1y&order=desc&limit=50`, pick(data).token, 'GET /v1/watchlist?sort=1y');
}

export function sparklines(data) {
  get(`${base}/v1/watchlist/sparklines?days=30`, pick(data).token, 'GET /v1/watchlist/sparklines');
}

// What the watchlist screen asks for when it opens (ADR-0038): the first page and the sparklines of every stock, together. The sparklines are not paged,
// so this is the case the question "do they need a cache of their own?" is about.
export function watchlist_page(data) {
  const { token } = pick(data);
  const tags = { phase: phase() };
  const started = Date.now();
  const [list, lines] = http.batch([
    ['GET', `${base}/v1/watchlist?limit=50`, null, { ...headers(token), tags: { ...tags, name: 'GET /v1/watchlist' } }],
    ['GET', `${base}/v1/watchlist/sparklines?days=30`, null, { ...headers(token), tags: { ...tags, name: 'GET /v1/watchlist/sparklines' } }],
  ]);
  pageTime.add(Date.now() - started, { endpoint: 'watchlist_page', ...tags });
  check(list, { 'page list answered 200': (r) => r.status === 200 });
  check(lines, { 'page sparklines answered 200': (r) => r.status === 200 });
}

export function portfolio_metrics(data) {
  const { token, portfolio } = pick(data);
  get(`${base}/v1/portfolios/${portfolio}/metrics`, token, 'GET /v1/portfolios/{id}/metrics');
}

export function calculator(data) {
  const { token } = pick(data);
  // Every virtual user walks the catalog with a different stride, so the stocks asked for are spread without any random number.
  const n = 1 + ((__VU * 7919 + __ITER * 104729) % stocks);
  const response = http.post(
    `${base}/v1/calculations/partial-purchase`,
    JSON.stringify({ market: 'XNAS', ticker: ticker(n), amount: '100' }),
    { ...headers(token), tags: { name: 'POST /v1/calculations/partial-purchase', phase: phase() } });
  check(response, { 'calculator answered 200': (r) => r.status === 200 });
}
