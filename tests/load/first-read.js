// The first read of the watchlist after the nightly load (ADR-0048), measured once per run: scripts/run-k6.sh runs it right after the stamps of the listings
// were renewed (the state after a load), once with nothing done to the cache and once after the warming job. Environment:
//   K6_BASE_URL  the API       K6_TOKEN  one access token       K6_SEED  "1" to put the 500 listings on this user's watchlist instead of reading
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

export const options = { vus: 1, iterations: 1, summaryTrendStats: ['avg', 'max'] };

const first = new Trend('first_read_ms', true);

const stocks = 500;

function seed() {
  for (let from = 1; from <= stocks; from += 25) {
    const requests = [];
    for (let n = from; n < from + 25 && n <= stocks; n++) {
      requests.push(['PUT', `${__ENV.K6_BASE_URL}/v1/watchlist/items/XNAS/T${String(n).padStart(4, '0')}`, JSON.stringify({ notes: null, targetPrice: null }),
        { headers: { Authorization: `Bearer ${__ENV.K6_TOKEN}`, 'Content-Type': 'application/json' } }]);
    }

    http.batch(requests).forEach((response) => check(response, { 'listing added': (r) => r.status === 200 || r.status === 201 }));
  }
}

// The costly view of ADR-0033: sorted by a window, so it needs the numbers of all the listings.
export default function () {
  if (__ENV.K6_SEED === '1') {
    seed();
    return;
  }

  const response = http.get(`${__ENV.K6_BASE_URL}/v1/watchlist?sort=1y&order=desc&limit=50`, {
    headers: { Authorization: `Bearer ${__ENV.K6_TOKEN}` },
  });
  check(response, { 'answered 200': (r) => r.status === 200 });
  first.add(response.timings.duration);
}
