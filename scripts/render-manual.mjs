import { readFileSync, writeFileSync } from "node:fs";
// Deliberately small renderer for the checked-in operator manual (no runtime dependency).
const esc = (s) =>
  s.replace(
    /[&<>\"]/g,
    (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c],
  );
const inline = (s) =>
  esc(s)
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/\[([^\]]+)\]\((https:\/\/[^)]+)\)/g, '<a href="$2">$1</a>');
let table = false,
  list = false;
let html = "";
for (const line of readFileSync("docs/SETUP-ANALYSIS.md", "utf8").split("\n")) {
  if (!line.startsWith("|") && table) {
    html += "</tbody></table></div>";
    table = false;
  }
  if (!/^\d+\. |^- /.test(line) && list) {
    html += "</ul>";
    list = false;
  }
  if (!line.trim()) continue;
  if (line.startsWith("|")) {
    if (/^\|[\s|:-]+$/.test(line)) continue;
    if (!table) {
      html += '<div class="table-scroll"><table><tbody>';
      table = true;
    }
    html +=
      "<tr>" +
      line
        .split("|")
        .slice(1, -1)
        .map((c) => "<td>" + inline(c.trim()) + "</td>")
        .join("") +
      "</tr>";
  } else if (/^#{1,3} /.test(line)) {
    const depth = line.indexOf(" ");
    html += `<h${depth}>${inline(line.slice(depth + 1))}</h${depth}>`;
  } else if (/^\d+\. |^- /.test(line)) {
    if (!list) {
      html += "<ul>";
      list = true;
    }
    html += "<li>" + inline(line.replace(/^- /, "")) + "</li>";
  } else html += "<p>" + inline(line) + "</p>";
}
if (list) html += "</ul>";
if (table) html += "</tbody></table></div>";
writeFileSync(
  "control/public/manual.html",
  `<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>RmsLink 설정·분석 매뉴얼</title><link rel="icon" href="/icon.svg"><link rel="stylesheet" href="/style.css"></head><body><main class="manual"><a href="/">← 대시보드</a>${html}</main></body></html>`,
);
