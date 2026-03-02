import http from 'k6/http';
import { check, sleep } from 'k6';

// Baseline scenario: steady-state load test
// 50 virtual users for 30 seconds
export const options = {
  vus: 50,
  duration: '30s',
  thresholds: {
    http_req_duration: ['p(95)<500'],  // p95 under 500ms
    http_req_failed: ['rate<0.01'],     // error rate under 1%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

export default function () {
  // GET /api/products — list all products (intentionally slow, no pagination)
  const listRes = http.get(`${BASE_URL}/api/products`);
  check(listRes, {
    'list products: status 200': (r) => r.status === 200,
    'list products: has data': (r) => {
      const body = r.json();
      return Array.isArray(body) && body.length > 0;
    },
  });

  sleep(0.5);

  // GET /api/products/{id} — get a single product
  const randomId = Math.floor(Math.random() * 100) + 1;
  const getRes = http.get(`${BASE_URL}/api/products/${randomId}`);
  check(getRes, {
    'get product: status 200 or 404': (r) => r.status === 200 || r.status === 404,
  });

  sleep(0.5);

  // GET /api/products/search?q=Product — search products (intentionally loads all)
  const searchRes = http.get(`${BASE_URL}/api/products/search?q=Product`);
  check(searchRes, {
    'search: status 200': (r) => r.status === 200,
  });

  sleep(0.5);

  // GET /api/products/by-category/Electronics — filter by category (N+1 pattern)
  const categoryRes = http.get(`${BASE_URL}/api/products/by-category/Electronics`);
  check(categoryRes, {
    'category filter: status 200': (r) => r.status === 200,
  });

  sleep(0.5);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

// k6 built-in text summary helper
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
