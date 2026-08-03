// Theme toggle, and the theme preference form on the account page.
//
// Three attributes on <html> carry the state, all set before first paint by the inline
// script in <head>:
//
//   data-theme-choice  auto | light | dark — which side of the toggle is showing, and what
//                      drives the button's icon
//   data-theme         light | dark, the Pico base. Always present: "auto" is resolved
//                      against the OS rather than left to prefers-color-scheme, so that a
//                      palette can be looked up by a definite mode
//   data-theme-name    the packaged palette keying a block in themes.css, absent when that
//                      side is set to Pico's stock look
//
// That inline script also exposes window.bohTheme so the resolve-and-apply logic lives in
// exactly one place. This file only wires events to it.
(function () {
    'use strict';

    var MODE_KEY = 'boh:theme';
    var PALETTE_KEY = 'boh:palettes';
    var CYCLE = ['auto', 'light', 'dark'];
    var LABELS = { auto: 'Theme: follow system', light: 'Theme: light', dark: 'Theme: dark' };

    var theme = window.bohTheme;
    if (!theme) return;

    function store(key, value) {
        try {
            localStorage.setItem(key, value);
        } catch (e) {
            // Storage unavailable (private mode, cookies blocked). The change still applies
            // for this page view; it just will not persist.
        }
    }

    var toggle = document.getElementById('theme-toggle');

    var label = function (choice) {
        if (!toggle) return;

        var text = LABELS[choice] || LABELS.auto;
        toggle.setAttribute('aria-label', text);
        toggle.setAttribute('title', text);
    };

    label(theme.choice());

    if (toggle) {
        toggle.addEventListener('click', function () {
            var next = CYCLE[(CYCLE.indexOf(theme.choice()) + 1) % CYCLE.length];

            theme.apply(next);
            label(next);
            store(MODE_KEY, next);
        });
    }

    // Following the OS only means anything if it keeps following it.
    theme.media.addEventListener('change', function () {
        if (theme.choice() === 'auto') theme.apply('auto');
    });

    // The account page's palette form. Signed in it posts to the server and this only
    // previews the change; with no account to store it against — BOH_AUTH_MODE=none —
    // localStorage is the whole of the persistence.
    var form = document.getElementById('theme-form');
    if (!form) return;

    var selects = form.querySelectorAll('select[data-theme-mode]');
    var local = form.dataset.themeStore === 'local';

    function palettes() {
        var map = {};
        Array.prototype.forEach.call(selects, function (select) {
            if (select.value) map[select.dataset.themeMode] = select.value;
        });
        return map;
    }

    // Without an account the server renders both selects as "Default", because it has
    // nothing to render them from. Fill them in from where the choice actually lives.
    if (local) {
        var stored = {};
        try {
            stored = JSON.parse(localStorage.getItem(PALETTE_KEY)) || {};
        } catch (e) { /* absent or corrupt; "Default" in both is the right fallback */ }

        Array.prototype.forEach.call(selects, function (select) {
            select.value = stored[select.dataset.themeMode] || '';
        });
    }

    // Live preview. Only the side currently showing can change on screen — picking a dark
    // scheme while in light mode does nothing visible until the toggle is used, which is
    // honest about what was actually selected.
    Array.prototype.forEach.call(selects, function (select) {
        select.addEventListener('change', function () {
            var map = palettes();

            theme.palettes(map);
            theme.apply(theme.choice());

            if (local) store(PALETTE_KEY, JSON.stringify(map));
        });
    });

    if (local) {
        form.addEventListener('submit', function (event) {
            event.preventDefault();
            store(PALETTE_KEY, JSON.stringify(palettes()));
        });
    }
})();

// Tag autocomplete: clicking a suggestion replaces the token being typed rather than the
// whole field, so a partly-written multi-tag query survives.
//
// Fields marked data-suggest-single hold exactly one tag (the tag-admin forms), so there a
// suggestion replaces the whole value.
(function () {
    'use strict';

    function inputFor(panel) {
        return document.querySelector('[data-suggest-for="#' + panel.id + '"]');
    }

    /*
       Every completing input sends its term as `q`, whatever the field is actually named.
       htmx would otherwise send the field's own name — `from`, `canonical`, `child` — and the
       endpoint would find no term and return nothing. Normalising here rather than teaching
       the endpoint six field names keeps the two ends from having to agree on form details.
    */
    document.addEventListener('htmx:configRequest', function (event) {
        var input = event.detail.elt;
        if (!input || !input.matches || !input.matches('[data-suggest-for]')) return;

        var params = event.detail.parameters;
        var name = input.getAttribute('name');

        // htmx 2 hands over a FormData; earlier versions a plain object.
        if (params && typeof params.set === 'function') {
            if (name) params.delete(name);
            params.set('q', input.value);
        } else if (params) {
            if (name) delete params[name];
            params.q = input.value;
        }
    });

    // Reflects dropdown state for screen readers on the fields that declare a combobox role.
    document.addEventListener('htmx:afterSwap', function (event) {
        var panel = event.target;
        if (!panel || !panel.classList || !panel.classList.contains('suggestions')) return;

        var input = inputFor(panel);
        if (input && input.hasAttribute('aria-expanded')) {
            input.setAttribute('aria-expanded', panel.childElementCount > 0 ? 'true' : 'false');
        }
    });

    function close(panel) {
        panel.innerHTML = '';

        var input = inputFor(panel);
        if (input && input.hasAttribute('aria-expanded')) {
            input.setAttribute('aria-expanded', 'false');
        }
    }

    function replaceLastToken(value, replacement) {
        var trailing = /\s$/.test(value);
        var tokens = value.split(/\s+/).filter(function (t) { return t.length > 0; });

        // A trailing space means the caret is on a fresh token, so nothing gets replaced.
        if (!trailing && tokens.length > 0) tokens.pop();

        tokens.push(replacement);
        return tokens.join(' ') + ' ';
    }

    // Delegated so it keeps working after HTMX swaps the suggestion list.
    document.addEventListener('click', function (event) {
        var button = event.target.closest('.suggestion');
        if (!button) return;

        var panel = button.closest('.suggestions');
        if (!panel) return;

        var input = inputFor(panel);
        if (!input) return;

        var tag = button.dataset.tag || '';

        // A single-tag field takes the whole value, and with no trailing space: it is submitted
        // as-is to a handler that parses one tag name, not a list.
        input.value = input.hasAttribute('data-suggest-single') ? tag : replaceLastToken(input.value, tag);

        close(panel);
        input.focus();
    });

    // Dismiss suggestions when focus moves elsewhere.
    document.addEventListener('click', function (event) {
        document.querySelectorAll('.suggestions').forEach(function (panel) {
            if (panel.contains(event.target)) return;

            var input = inputFor(panel);
            if (input && input === event.target) return;

            close(panel);
        });
    });

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') return;
        document.querySelectorAll('.suggestions').forEach(close);
    });
})();

// Live row filter for the tag-admin tables. Client-side because every row is already in the
// document: a round trip per keystroke would be slower and no more accurate.
(function () {
    'use strict';

    function rowText(row) {
        // The actions cell is excluded deliberately — every row contains the word "Remove",
        // so including it would make that term match everything.
        return Array.prototype.filter
            .call(row.cells, function (cell) { return !cell.classList.contains('row-actions'); })
            .map(function (cell) { return cell.textContent; })
            .join(' ')
            .toLowerCase();
    }

    function apply(input) {
        var table = document.querySelector(input.dataset.filterTable);
        if (!table) return;

        var term = input.value.trim().toLowerCase();
        var rows = table.tBodies.length ? table.tBodies[0].rows : [];
        var shown = 0;
        var total = 0;
        var empty = null;

        Array.prototype.forEach.call(rows, function (row) {
            if (row.classList.contains('filter-empty')) {
                empty = row;
                return;
            }

            total++;
            var match = term === '' || rowText(row).indexOf(term) !== -1;
            row.hidden = !match;
            if (match) shown++;
        });

        if (empty) empty.hidden = shown > 0;

        var status = input.dataset.filterStatus && document.querySelector(input.dataset.filterStatus);
        if (status) {
            status.textContent = term === ''
                ? total + ' row' + (total === 1 ? '' : 's')
                : shown + ' of ' + total + ' shown';
        }
    }

    function init() {
        document.querySelectorAll('[data-filter-table]').forEach(function (input) {
            input.addEventListener('input', function () { apply(input); });

            // Escape clears rather than only closing something, since there is no dropdown here.
            input.addEventListener('keydown', function (event) {
                if (event.key !== 'Escape' || input.value === '') return;
                input.value = '';
                apply(input);
            });

            // A value restored by the browser on reload must take effect without a keystroke.
            if (input.value !== '') apply(input);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
