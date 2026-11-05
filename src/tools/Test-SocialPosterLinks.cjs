const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname,
    '../ShortenerTools.Admin/wwwroot/js/socialposter-links.js'), 'utf8');

function host(initial, writable = true) {
    let saved = initial == null ? null : JSON.stringify(initial);
    const window = { socialPoster: {
        loadDraft: () => saved,
        saveDraft: json => { if (writable) saved = json; }
    }};
    vm.runInNewContext(source, { window });
    return { compose: window.shortenerComposeLink, draft: () => JSON.parse(saved) };
}

const url = 'https://go.example/my-event';
const fresh = host(null);
fresh.compose(url, 'Title', 'Message');
assert.equal(fresh.draft().Base, `Title\n\nMessage\n${url}`);

const existing = host({ Base: 'Unfinished draft', SourceUrl: 'https://old.example', WorkflowState: '{}', Overrides: { 0: 'X version', 1: null },
    Selected: { 0: false, 1: true }, CustomizedTexts: { 0: 'Custom X', 1: null } });
existing.compose(url, 'Do not replace my draft', 'Metadata');
assert.equal(existing.draft().Base, `Unfinished draft\n${url}`);
assert.equal(existing.draft().Overrides[0], `X version\n${url}`);
assert.equal(existing.draft().CustomizedTexts[0], `Custom X\n${url}`);
assert.equal(existing.draft().Overrides[1], null);
assert.deepEqual(existing.draft().Selected, { 0: false, 1: true });
assert.equal(existing.draft().SourceUrl, '');
assert.equal(existing.draft().WorkflowState, null);
existing.compose(url);
assert.equal(existing.draft().Base, `Unfinished draft\n${url}`);

const unavailable = host(null, false);
assert.throws(() => unavailable.compose(url), /could not be saved/);
console.log('SocialPoster draft handoff checks passed.');
