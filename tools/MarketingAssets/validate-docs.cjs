const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(process.argv[2]);
const files = ['README.md', 'README.pl.md', 'SECURITY.md', 'CONTRIBUTING.md', 'docs/PROMOTION.md', 'tools/MarketingAssets/README.md'];
let checked = 0;
for (const file of files) {
  const text = fs.readFileSync(path.join(root, file), 'utf8');
  for (const match of text.matchAll(/\]\(([^\s)]+)(?:\s+[^)]*)?\)/g)) {
    const target = match[1];
    if (/^(https?:|#|mailto:)/.test(target)) continue;
    const resolved = path.resolve(root, path.dirname(file), decodeURIComponent(target.split('#')[0]));
    if (!fs.existsSync(resolved)) throw Error(`Broken local link in ${file}: ${target}`);
    checked++;
  }
}
const yaml = require('yaml');
for (const name of ['bug_report.yml', 'feature_request.yml', 'config.yml']) {
  const parsed = yaml.parse(fs.readFileSync(path.join(root, '.github/ISSUE_TEMPLATE', name), 'utf8'));
  if (name !== 'config.yml' && (!parsed.name || !Array.isArray(parsed.body))) throw Error('Invalid issue form: ' + name);
}
if (!fs.readFileSync(path.join(root, 'LICENSE'), 'utf8').includes('Marcus Jensen')) throw Error('Upstream attribution missing');
if (!fs.readFileSync(path.join(root, 'README.md'), 'utf8').includes('Marcus Jensen')) throw Error('README attribution missing');
console.log(`Validated ${checked} local documentation links, 3 issue templates and upstream attribution.`);
