// Theme toggle and the account page's theme form.
// State lives on <html>, set before paint by the inline script in <head>:
// data-theme-choice (auto|light|dark), data-theme (resolved Pico base) and
// data-theme-name (palette, absent for stock). That script exposes window.bohTheme;
// this file only wires events to it.
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
            // Storage unavailable: applies for this page view only.
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

    theme.media.addEventListener('change', function () {
        if (theme.choice() === 'auto') theme.apply('auto');
    });

    // Signed in, the form posts and this only previews; with auth off, localStorage is the persistence.
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

    // With no account the server can't prefill the selects; fill them from localStorage.
    if (local) {
        var stored = {};
        try {
            stored = JSON.parse(localStorage.getItem(PALETTE_KEY)) || {};
        } catch (e) { /* absent or corrupt; "Default" in both is the right fallback */ }

        Array.prototype.forEach.call(selects, function (select) {
            select.value = stored[select.dataset.themeMode] || '';
        });
    }

    // Live preview of the side currently showing.
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

// Tag autocomplete: a suggestion replaces the token being typed (or the whole value for
// data-suggest-single). Keyboard: Up/Down walk, Enter takes, Escape/Tab dismiss. Nothing is
// highlighted until an arrow key, so Enter still submits.
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

    // Every completing input sends its term as `q`, whatever the field is named.
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

    // ARIA state and row ids, made unique per panel here.
    document.addEventListener('htmx:afterSwap', function (event) {
        var panel = event.target;
        if (!panel || !panel.classList || !panel.classList.contains('suggestions')) return;

        options(panel).forEach(function (option, index) {
            option.id = panel.id + '-option-' + index;
        });

        var input = inputFor(panel);
        if (!input) return;

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
            if (option.scrollIntoView) option.scrollIntoView({ block: 'nearest' });
        } else {
            input.removeAttribute('aria-activedescendant');
        }
    }

    function move(panel, delta) {
        var list = options(panel);
        if (list.length === 0) return;

        var current = list.indexOf(highlighted(panel));

        // Wraps at both ends.
        var next = current === -1
            ? (delta > 0 ? 0 : list.length - 1)
            : (current + delta + list.length) % list.length;

        highlight(panel, list[next]);
    }

    function replaceLastToken(value, replacement) {
        var trailing = /\s$/.test(value);
        var tokens = value.split(/\s+/).filter(function (t) { return t.length > 0; });

        // Trailing space: nothing to replace.
        if (!trailing && tokens.length > 0) tokens.pop();

        tokens.push(replacement);
        return tokens.join(' ') + ' ';
    }

    function take(panel, option) {
        var input = inputFor(panel);
        if (!input) return;

        var tag = option.dataset.tag || '';

        // Single-tag fields take the whole value, no trailing space.
        input.value = input.hasAttribute('data-suggest-single') ? tag : replaceLastToken(input.value, tag);

        close(panel);
        input.focus();
    }

    document.addEventListener('click', function (event) {
        var button = event.target.closest('.suggestion');
        if (!button) return;

        var panel = button.closest('.suggestions');
        if (panel) take(panel, button);
    });

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

        // Tab dismisses and lets focus move.
        if (event.key === 'Tab') {
            close(panel);
            return;
        }

        if (options(panel).length === 0) return;

        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            // Keeps the caret in place.
            event.preventDefault();
            move(panel, event.key === 'ArrowDown' ? 1 : -1);
            return;
        }

        if (event.key === 'Enter') {
            var option = highlighted(panel);
            if (!option) return;

            event.preventDefault();
            take(panel, option);
        }
    });
})();

// Client-side row filter for the tag-admin tables.
(function () {
    'use strict';

    function rowText(row) {
        // Skip the actions cell: every row says "Remove".
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

            input.addEventListener('keydown', function (event) {
                if (event.key !== 'Escape' || input.value === '') return;
                input.value = '';
                apply(input);
            });

            // Apply a value the browser restored on reload.
            if (input.value !== '') apply(input);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

// Clears a form after its own successful submit. Replaces hx-on attributes, which the
// CSP blocks; delegated because htmx swaps the forms.
(function () {
    'use strict';

    document.addEventListener('htmx:afterRequest', function (event) {
        var form = event.target;

        // Only the form itself: its autocomplete input issues requests on every keystroke.
        if (!(form instanceof HTMLFormElement)) return;
        if (!form.hasAttribute('data-reset-on-success')) return;
        if (!event.detail || !event.detail.successful) return;

        form.reset();
    });
})();

// Confirms destructive submits; inline onsubmit is blocked by the CSP.
(function () {
    'use strict';

    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (!(form instanceof HTMLFormElement)) return;

        var message = form.getAttribute('data-confirm');
        if (message && !window.confirm(message)) event.preventDefault();
    });
})();
