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

// Tag autocomplete: taking a suggestion replaces the token being typed rather than the whole
// field, so a partly-written multi-tag query survives.
//
// Fields marked data-suggest-single hold exactly one tag (the tag-admin forms), so there a
// suggestion replaces the whole value.
//
// The list is reachable from the keyboard as well as the mouse: Down and Up walk it, Enter
// takes the highlighted row, Escape and Tab dismiss it. Nothing is highlighted until an
// arrow key is pressed — so Enter on a freshly typed term still submits the form, which is
// what someone who typed the whole tag out expects.
(function () {
    'use strict';

    var ACTIVE = 'is-active';

    function inputFor(panel) {
        return document.querySelector('[data-suggest-for="#' + panel.id + '"]');
    }

    function panelFor(input) {
        var selector = input.getAttribute('data-suggest-for');
        return selector ? document.querySelector(selector) : null;
    }

    function options(panel) {
        return Array.prototype.slice.call(panel.querySelectorAll('.suggestion'));
    }

    function highlighted(panel) {
        return panel.querySelector('.suggestion.' + ACTIVE);
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

    // Reflects dropdown state for screen readers, and stamps the row ids that
    // aria-activedescendant points at. Those ids cannot come from the fragment: one partial
    // serves every panel on the page, so uniqueness is only knowable here, from the panel.
    document.addEventListener('htmx:afterSwap', function (event) {
        var panel = event.target;
        if (!panel || !panel.classList || !panel.classList.contains('suggestions')) return;

        options(panel).forEach(function (option, index) {
            option.id = panel.id + '-option-' + index;
        });

        var input = inputFor(panel);
        if (!input) return;

        // A fresh list invalidates whatever was highlighted against the previous one.
        input.removeAttribute('aria-activedescendant');

        if (input.hasAttribute('aria-expanded')) {
            input.setAttribute('aria-expanded', panel.childElementCount > 0 ? 'true' : 'false');
        }
    });

    function close(panel) {
        panel.innerHTML = '';

        var input = inputFor(panel);
        if (!input) return;

        input.removeAttribute('aria-activedescendant');

        if (input.hasAttribute('aria-expanded')) {
            input.setAttribute('aria-expanded', 'false');
        }
    }

    function highlight(panel, option) {
        options(panel).forEach(function (candidate) {
            var on = candidate === option;
            candidate.classList.toggle(ACTIVE, on);
            candidate.setAttribute('aria-selected', on ? 'true' : 'false');
        });

        var input = inputFor(panel);
        if (!input) return;

        if (option) {
            input.setAttribute('aria-activedescendant', option.id);
            // The list caps at roughly eight rows and scrolls past that, so walking off the
            // bottom has to bring the row into view.
            if (option.scrollIntoView) option.scrollIntoView({ block: 'nearest' });
        } else {
            input.removeAttribute('aria-activedescendant');
        }
    }

    function move(panel, delta) {
        var list = options(panel);
        if (list.length === 0) return;

        var current = list.indexOf(highlighted(panel));

        // From nothing, Down starts at the top and Up at the bottom; both ends wrap round.
        var next = current === -1
            ? (delta > 0 ? 0 : list.length - 1)
            : (current + delta + list.length) % list.length;

        highlight(panel, list[next]);
    }

    function replaceLastToken(value, replacement) {
        var trailing = /\s$/.test(value);
        var tokens = value.split(/\s+/).filter(function (t) { return t.length > 0; });

        // A trailing space means the caret is on a fresh token, so nothing gets replaced.
        if (!trailing && tokens.length > 0) tokens.pop();

        tokens.push(replacement);
        return tokens.join(' ') + ' ';
    }

    function take(panel, option) {
        var input = inputFor(panel);
        if (!input) return;

        var tag = option.dataset.tag || '';

        // A single-tag field takes the whole value, and with no trailing space: it is submitted
        // as-is to a handler that parses one tag name, not a list.
        input.value = input.hasAttribute('data-suggest-single') ? tag : replaceLastToken(input.value, tag);

        close(panel);
        input.focus();
    }

    // Delegated so it keeps working after HTMX swaps the suggestion list.
    document.addEventListener('click', function (event) {
        var button = event.target.closest('.suggestion');
        if (!button) return;

        var panel = button.closest('.suggestions');
        if (panel) take(panel, button);
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
        if (event.key === 'Escape') {
            document.querySelectorAll('.suggestions').forEach(close);
            return;
        }

        var input = event.target;
        if (!input || !input.matches || !input.matches('[data-suggest-for]')) return;

        var panel = panelFor(input);
        if (!panel) return;

        // Tab is the exception that acts on an already-empty panel: it dismisses on the way
        // out, and leaves the focus move itself alone.
        if (event.key === 'Tab') {
            close(panel);
            return;
        }

        if (options(panel).length === 0) return;

        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            // Otherwise the caret jumps to the far end of the field as the list moves.
            event.preventDefault();
            move(panel, event.key === 'ArrowDown' ? 1 : -1);
            return;
        }

        if (event.key === 'Enter') {
            var option = highlighted(panel);
            if (!option) return;

            // Only swallow the submit when there is a pick to apply.
            event.preventDefault();
            take(panel, option);
        }
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

// Clears a form once its own submission succeeds.
//
// This replaces an hx-on::after-request attribute on each such form. htmx evaluates those
// with new Function, which the content security policy does not allow, so the behaviour
// moves here — delegated from the document, since the forms live inside markup htmx swaps
// out and a listener bound to one would not survive.
(function () {
    'use strict';

    document.addEventListener('htmx:afterRequest', function (event) {
        var form = event.target;

        // event.target is whatever issued the request, and htmx events bubble. The tag form
        // contains an input that fetches autocomplete suggestions on every keystroke, so
        // matching anything but the form itself would reset it mid-typing.
        if (!(form instanceof HTMLFormElement)) return;
        if (!form.hasAttribute('data-reset-on-success')) return;
        if (!event.detail || !event.detail.successful) return;

        form.reset();
    });
})();

// Asks before a destructive form submits. Replaces inline onsubmit handlers, which the
// content security policy refuses to run.
(function () {
    'use strict';

    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (!(form instanceof HTMLFormElement)) return;

        var message = form.getAttribute('data-confirm');
        if (message && !window.confirm(message)) event.preventDefault();
    });
})();
