// Load baseline of the Phase 2 read paths (milestone 2.8, ADR-0041). Run it with scripts/run-k6.sh, which builds the throwaway stack,
// seeds 500 listings with six years of prices, starts the API and mints the tokens; running it by hand needs the same environment:
//   K6_BASE_URL   the API, for example http://127.0.0.1:58090
//   K6_TOKENS     comma-separated access tokens, one per user
//   K6_ENFORCE    "1" to fail the run when a threshold is crossed (the budgets of ADR-0033); anything else only reports
//   K6_PHASE_SECONDS  seconds each scenario runs (default 30)
// The scenarios run one after the other, so each number is that endpoint alone on the machine and not a mix.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const base = __ENV.K6_BASE_URL;
const tokens = (__ENV.K6_TOKENS || '').split(',').filter((t) => t.length > 0);
const seconds = parseInt(__ENV.K6_PHASE_SECONDS || '30', 10);
const enforce = __ENV.K6_ENFORCE === '1';
const stocks = 500;
const portfolioStocks = 100;
const vus = parseInt(__ENV.K6_VUS || '4', 10);

const scenarioNames = ['watchlist_by_ticker', 'watchlist_by_performance', 'sparklines', 'portfolio_metrics', 'calculator'];

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
  // The budget of the whole watchlist is 300 ms (ADR-0030, ADR-0033); the others are held to the same figure.
  thresholds: Object.fromEntries(scenarioNames.flatMap((name) => [
    [`http_req_duration{endpoint:${name}}`, enforce ? ['p(95)<300'] : []],
    [`http_req_failed{endpoint:${name}}`, enforce ? ['rate<0.01'] : []],
    [`http_reqs{endpoint:${name}}`, ['count>=0']],
  ])),
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const settled = new Trend('settled_ms');

function headers(token, extra = {}) {
  return { headers: Object.assign({ Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, extra) };
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
    for (let from = 1; from <= portfolioStocks; from += 25) {
      const requests = [];
      for (let n = from; n < from + 25 && n <= portfolioStocks; n++) {
        requests.push(['PUT', `${base}/v1/portfolios/${id}/items/XNAS/${ticker(n)}`, null, headers(token)]);
      }

      http.batch(requests).forEach((response) => ok(response, 'put portfolio item'));
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

function get(url, token, name) {
  const response = http.get(url, Object.assign(headers(token), { tags: { name } }));
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

export function portfolio_metrics(data) {
  const { token, portfolio } = pick(data);
  get(`${base}/v1/portfolios/${portfolio}/metrics`, token, 'GET /v1/portfolios/{id}/metrics');
}

export function calculator(data) {
  const { token } = pick(data);
  const n = 1 + Math.floor(Math.random() * stocks);
  const response = http.post(
    `${base}/v1/calculations/partial-purchase`,
    JSON.stringify({ market: 'XNAS', ticker: ticker(n), amount: '100' }),
    Object.assign(headers(token), { tags: { name: 'POST /v1/calculations/partial-purchase' } }));
  check(response, { 'calculator answered 200': (r) => r.status === 200 });
}
