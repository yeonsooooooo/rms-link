import test from "node:test";
import assert from "node:assert/strict";
import { readingMethods } from "../control/reading-methods.mjs";
const methods = (status, lineCount = 0, candidateCount = 0) => ({
  methods: {
    uia: { status, lineCount, candidateCount, acceptedCount: 0 },
    ocr: { status: "ok", lineCount: 3, candidateCount: 2, acceptedCount: 1 },
  },
  readings: [],
});
test("plain language distinguishes skipped, failed, empty, unparsed and usable direct reading", () => {
  assert.match(readingMethods(null).detail, /이전 버전/);
  assert.match(
    readingMethods(methods("skipped_region")).detail,
    /검사하지 않았/,
  );
  assert.match(readingMethods(methods("timeout")).detail, /영구적으로/);
  assert.match(readingMethods(methods("error")).detail, /실행 권한/);
  assert.match(readingMethods(methods("empty")).title, /가져온 글자 없음/);
  assert.match(readingMethods(methods("ok", 3, 0)).title, /해석 필요/);
  assert.match(readingMethods(methods("ok", 3, 2)).title, /직접 가져옴/);
  const partial = methods("ok", 3, 2);
  partial.readings = [{ source: "ocr" }];
  assert.match(readingMethods(partial).title, /부족한 부분은 OCR/);
});
