// Editable source for the Unity MCP Lite logo and Unity Editor icons.
// Run from website/: node scripts/gen-light-brand-assets.mjs
import sharp from 'sharp';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

function mark(orange, copper, neutral) {
  return `<g fill="none" stroke-width="15" stroke-linejoin="round">
    <path d="M108 44 L35 86 L35 167" stroke="${copper}"/>
    <path d="M148 44 L221 86 L221 167" stroke="${orange}"/>
    <path d="M128 62 V108" stroke="${copper}" stroke-linecap="round"/>
    <path d="M58 101 L128 142 V212" stroke="${copper}" stroke-linecap="round"/>
    <path d="M128 142 L198 101" stroke="${neutral}" stroke-linecap="round"/>
    <path d="M62 204 L128 242" stroke="${copper}"/>
    <path d="M128 242 L194 204" stroke="${neutral}" stroke-linecap="round"/>
  </g>
  <circle cx="128" cy="28" r="21" fill="${orange}"/>
  <circle cx="33" cy="194" r="21" fill="${copper}"/>
  <circle cx="223" cy="194" r="21" fill="${neutral}"/>`;
}

for (const dark of [false, true]) {
  const suffix = dark ? '-dark' : '';
  const orange = dark ? '#E66B4C' : '#CE422B';
  const copper = dark ? '#E9B08C' : '#B86B43';
  const neutral = dark ? '#D7DBDE' : '#4B5E6C';
  const text = dark ? '#D7DBDE' : '#28343E';
  const icon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Unity MCP Lite Rust logo"><title>Unity MCP Lite Rust logo</title>${mark(orange, copper, neutral)}</svg>\n`;
  const logo = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1088 780" role="img" aria-label="Unity MCP Lite"><title>Unity MCP Lite</title>
  <g transform="translate(322 27) scale(1.73)">${mark(orange, copper, neutral)}</g>
  <text x="544" y="662" text-anchor="middle" font-family="Arial, Helvetica, sans-serif" font-size="138" font-weight="600" fill="${text}">Unity MCP <tspan fill="${orange}">Lite</tspan></text>
  <text x="544" y="742" text-anchor="middle" font-family="Arial, Helvetica, sans-serif" font-size="28" letter-spacing="12" fill="${text}">CONNECT <tspan fill="${orange}">•</tspan> EXTEND <tspan fill="${orange}">•</tspan> BUILD LIGHTER</text>
</svg>\n`;
  const iconPath = path.join(root, `docs/images/unity-mcp-light-mark${suffix}.svg`);
  const logoPath = path.join(root, `docs/images/unity-mcp-light-logo${suffix}.svg`);
  await writeFile(iconPath, icon);
  await writeFile(logoPath, logo);
  await sharp(Buffer.from(icon)).resize(256, 256).png().toFile(path.join(root, `MCPForUnity/package-icon${suffix}.png`));
  await sharp(Buffer.from(logo)).resize(1088, 780).png().toFile(path.join(root, `docs/images/unity-mcp-light-logo${suffix}.png`));
  console.log(`Generated ${dark ? 'dark' : 'light'} Rust logo and Unity icon.`);
}
