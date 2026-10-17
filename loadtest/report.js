// Turns a k6 --summary-export file into a Markdown table and a one-line GitHub annotation.
//   node loadtest/report.js summary.json "<description of the machine>"

const fs = require('fs');

const summary = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const machine = process.argv[3] || 'unknown machine';
const m = summary.metrics;

const duration = m['http_req_duration{scenario:payments}'] || m.http_req_duration;
const failed = m['http_req_failed{scenario:payments}'] || m.http_req_failed;
const iterations = m.iterations;
const unexpected = m.payments_unexpected_outcome;
const failedOver = m.payments_failed_over;

const ms = (v) => `${Number(v).toFixed(1)} ms`;
const pct = (v) => `${(Number(v) * 100).toFixed(2)}%`;

const rows = [
  ['Payments created', `${iterations.count}`],
  ['Throughput', `${Number(iterations.rate).toFixed(1)} payments/s`],
  ['Latency p50', ms(duration.med)],
  ['Latency p95', ms(duration['p(95)'])],
  ['Latency p99', ms(duration['p(99)'])],
  ['HTTP errors', pct(failed.value ?? failed.rate ?? 0)],
  ['Unexpected payment outcomes', pct(unexpected.value ?? unexpected.rate ?? 0)],
  ['Payments failed over during outage', `${failedOver ? failedOver.count : 0}`],
];

console.log(`## Load test results\n\nRun on ${machine}. Each payment is persisted, routed to a simulated provider, captured, posted to the ledger and published through the outbox.\n`);
console.log('| Metric | Value |');
console.log('| --- | --- |');
for (const [k, v] of rows) {
  console.log(`| ${k} | ${v} |`);
}

const line = rows.map(([k, v]) => `${k}: ${v}`).join('; ');
fs.writeFileSync(process.argv[4] || 'k6-notice.txt', `::notice title=k6 results::${line}\n`);
