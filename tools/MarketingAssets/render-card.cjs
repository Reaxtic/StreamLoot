// Reproducible, code-native artwork using the existing project logo and current UI.
// npm install --no-save sharp in a separate tools environment, then:
// NODE_PATH=<node_modules> node tools/MarketingAssets/render-card.cjs <repo-root>
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(process.argv[2]);
const assets = path.join(root, 'Github-Assets');
const data = name => 'data:image/png;base64,' + fs.readFileSync(path.join(assets, name)).toString('base64');
const logo = data('logo.png');
const preview = data('dashboard-current.png');
const social = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="640" viewBox="0 0 1280 640">
<defs><linearGradient id="bg"><stop stop-color="#0e1014"/><stop offset="1" stop-color="#10333d"/></linearGradient><clipPath id="screen"><rect x="650" y="154" width="580" height="344" rx="14"/></clipPath></defs>
<rect width="1280" height="640" fill="url(#bg)"/><rect x="0" width="8" height="640" fill="#22d3ee"/>
<image href="${logo}" x="54" y="48" width="72" height="72"/>
<g font-family="Segoe UI,Arial,sans-serif"><text x="146" y="96" font-size="40" font-weight="700" fill="#f4f6f8">Stream Loot</text>
<text x="56" y="211" font-size="53" font-weight="700" fill="#f4f6f8">Twitch + Kick.</text>
<text x="56" y="277" font-size="53" font-weight="700" fill="#22d3ee">One drops app.</text>
<text x="58" y="336" font-size="24" fill="#c8d0dc">Game priorities. Campaign pins.</text>
<text x="58" y="374" font-size="24" fill="#c8d0dc">Reward progress in one place.</text>
<rect x="56" y="430" width="316" height="48" rx="12" fill="#22d3ee"/><text x="80" y="462" font-size="23" font-weight="700" fill="#0e1014">Free · Open source · MIT</text>
<rect x="648" y="152" width="584" height="348" rx="16" fill="#242932"/><image href="${preview}" x="650" y="154" width="580" height="344" clip-path="url(#screen)"/>
<text x="650" y="528" font-size="16" fill="#9ba3af">Current interface · illustrative demo data</text>
<text x="58" y="585" font-size="23" fill="#f4f6f8">github.com/Reaxtic/StreamLoot</text><text x="1010" y="585" font-size="21" fill="#9ba3af">Windows x64</text></g></svg>`;
const banner = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="280">
<rect width="1280" height="280" rx="18" fill="#14181e"/><rect x="0" y="0" width="8" height="280" fill="#22d3ee"/>
<image href="${logo}" x="48" y="60" width="110" height="110"/>
<g font-family="Segoe UI,Arial,sans-serif"><text x="192" y="94" fill="#f4f6f8" font-size="44" font-weight="700">Stream Loot</text>
<text x="192" y="141" fill="#22d3ee" font-size="28">Twitch &amp; Kick Drops Miner for Windows</text>
<text x="192" y="187" fill="#c8d0dc" font-size="22">Free · Open source · MIT licensed</text>
<text x="192" y="236" fill="#9ba3af" font-size="20">github.com/Reaxtic/StreamLoot</text></g></svg>`;
(async () => {
  for (const [name, svg] of [['social-preview', social], ['banner', banner]]) {
    fs.writeFileSync(path.join(assets, name + '.svg'), svg);
    await sharp(Buffer.from(svg)).png().toFile(path.join(assets, name + '.png'));
  }
  const info = await sharp(path.join(assets, 'social-preview.png')).metadata();
  if (info.width !== 1280 || info.height !== 640 || fs.statSync(path.join(assets, 'social-preview.png')).size >= 1000000)
    throw Error('Social preview does not meet the intended dimensions/size.');
  console.log('Rendered social-preview.png and banner.png.');
})();
