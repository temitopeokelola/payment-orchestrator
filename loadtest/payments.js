// Load test: constant arrival rate of payment requests, with a provider outage in the middle.
//
//   k6 run loadtest/payments.js
//   RATE=200 DURATION=2m k6 run loadtest/payments.js
//
// Expects the stack from docker-compose.yml (Development environment, so the simulator controls are available).

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const API_KEY = __ENV.API_KEY || 'sk_test_demo_merchant_key';
const RATE = Number(__ENV.RATE || 100);
const DURATION = __ENV.DURATION || '60s';
const OUTAGE_START = __ENV.OUTAGE_START || '20s';
const OUTAGE_SECONDS = Number(__ENV.OUTAGE_SECONDS || 20);

// A payment whose final status was not what its test card should produce.
const unexpectedOutcome = new Rate('payments_unexpected_outcome');
const failedOver = new Counter('payments_failed_over');

export const options = {
  scenarios: {
    payments: {
      executor: 'constant-arrival-rate',
      exec: 'createPayment',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 50,
      maxVUs: 400,
    },
    outage: {
      executor: 'shared-iterations',
      exec: 'providerOutage',
      vus: 1,
      iterations: 1,
      startTime: OUTAGE_START,
      maxDuration: `${OUTAGE_SECONDS + 30}s`,
    },
  },
  thresholds: {
    'http_req_failed{scenario:payments}': ['rate<0.01'],
    'http_req_duration{scenario:payments}': ['p(95)<500'],
    payments_unexpected_outcome: ['rate<0.01'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const headers = (idempotencyKey) => ({
  'Content-Type': 'application/json',
  Authorization: `Bearer ${API_KEY}`,
  'Idempotency-Key': idempotencyKey,
});

function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

export function createPayment() {
  // 95% good cards, 5% declines: declines are a normal outcome, not an error.
  const declined = Math.random() < 0.05;
  const body = JSON.stringify({
    amount: 1000 + Math.floor(Math.random() * 500000),
    currency: 'NGN',
    description: 'k6 load test',
    paymentMethod: declined ? 'sim_card_declined' : 'sim_card_approved',
    customerEmail: 'load@example.com',
  });

  const res = http.post(`${BASE_URL}/v1/payments`, body, { headers: headers(uuid()), tags: { name: 'POST /v1/payments' } });

  const payment = res.status === 201 ? res.json() : null;
  const expected = declined ? 'failed' : 'captured';
  const ok = check(res, {
    'status is 201': (r) => r.status === 201,
    'payment reached expected status': () => payment !== null && payment.status === expected,
  });

  unexpectedOutcome.add(!ok);
  if (payment && payment.provider === 'simulator-b') {
    failedOver.add(1);
  }
}

export function providerOutage() {
  const json = { 'Content-Type': 'application/json' };
  http.post(`${BASE_URL}/dev/simulators/simulator-a/outage`, JSON.stringify({ down: true }), { headers: json });
  sleep(OUTAGE_SECONDS);
  http.post(`${BASE_URL}/dev/simulators/simulator-a/outage`, JSON.stringify({ down: false }), { headers: json });
}
