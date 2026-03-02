import http from 'k6/http';
import { check, sleep } from 'k6';

// Stress scenario: progressive ramp-up to find breaking points
// Ramps from 10 to 200 VUs over 2 minutes
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 50 },   // Normal load
    { duration: '30s', target: 100 },  // High load
    { duration: '30s', target: 200 },  // Stress load
    { duration: '15s', target: 0 },    // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<1000'],  // p95 under 1s (lenient for stress)
    http_req_failed: ['rate<0.05'],      // error rate under 5%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

export default function () {
  // Mix of endpoints to simulate realistic marketplace traffic
  const randomId = Math.floor(Math.random() * 100) + 1;
  const sessionId = `k6-stress-${__VU}-${__ITER}`;

  const endpoints = [
    // Product endpoints
    `${BASE_URL}/api/products`,
    `${BASE_URL}/api/products/${randomId}`,
    `${BASE_URL}/api/products/search?q=Product`,
    `${BASE_URL}/api/products/by-category/Electronics`,
    `${BASE_URL}/api/categories`,
    // Review endpoints
    `${BASE_URL}/api/reviews/by-product/${Math.floor(Math.random() * 500) + 1}`,
    `${BASE_URL}/api/reviews/average/${Math.floor(Math.random() * 500) + 1}`,
    // Cart endpoint
    `${BASE_URL}/api/cart/${sessionId}`,
    // Order endpoints
    `${BASE_URL}/api/orders`,
    // Razor Pages
    `${BASE_URL}/`,
    `${BASE_URL}/Products`,
    `${BASE_URL}/Products/Detail/${randomId}`,
    `${BASE_URL}/Cart`,
    `${BASE_URL}/Orders`,
  ];

  const endpoint = endpoints[Math.floor(Math.random() * endpoints.length)];
  const res = http.get(endpoint);

  check(res, {
    'status is 200 or 404': (r) => r.status === 200 || r.status === 404,
  });

  sleep(Math.random() * 0.5 + 0.1); // Random 100-600ms think time
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
