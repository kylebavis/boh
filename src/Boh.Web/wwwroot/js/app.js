// Theme toggle: cycles auto -> light -> dark -> auto.
//
// "auto" is represented by the *absence* of data-theme, which is what lets Pico's
// prefers-color-scheme rules take over. data-theme-choice always reflects the user's
// selection so CSS can show the matching icon even in the auto case.
//
// The initial attributes are set by an inline script in <head>; this file only handles
// interaction and label sync, so it is safe to load deferred.
(function () {
    'use strict';

    var STORAGE_KEY = 'boh:theme';
    var ORDER = ['auto', 'light', 'dark'];
    var LABELS = {
        auto: 'Theme: follow system',
        light: 'Theme: light',
        dark: 'Theme: dark'
    };

    function readChoice() {
        try {
            var stored = localStorage.getItem(STORAGE_KEY);
            return stored === 'light' || stored === 'dark' ? stored : 'auto';
        } catch (e) {
            return 'auto';
        }
    }

    function writeChoice(choice) {
        try {
            if (choice === 'auto') localStorage.removeItem(STORAGE_KEY);
            else localStorage.setItem(STORAGE_KEY, choice);
        } catch (e) {
            // Storage unavailable (private mode, cookies blocked). The theme still
            // applies for this page view; it just will not persist.
        }
    }

    function apply(choice) {
        var root = document.documentElement;

        root.dataset.themeChoice = choice;
        if (choice === 'auto') delete root.dataset.theme;
        else root.dataset.theme = choice;

        var button = document.getElementById('theme-toggle');
        if (button) {
            button.setAttribute('aria-label', LABELS[choice]);
            button.setAttribute('title', LABELS[choice]);
        }
    }

    function init() {
        // Sync the button's label with whatever the inline script already applied.
        apply(readChoice());

        var button = document.getElementById('theme-toggle');
        if (!button) return;

        button.addEventListener('click', function () {
            var next = ORDER[(ORDER.indexOf(readChoice()) + 1) % ORDER.length];
            writeChoice(next);
            apply(next);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

// Tag autocomplete: clicking a suggestion replaces the token being typed rather than the
// whole field, so a partly-written multi-tag query survives.
(function () {
    'use strict';

    function inputFor(panel) {
        return document.querySelector('[data-suggest-for="#' + panel.id + '"]');
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

        input.value = replaceLastToken(input.value, button.dataset.tag || '');
        panel.innerHTML = '';
        input.focus();
    });

    // Dismiss suggestions when focus moves elsewhere.
    document.addEventListener('click', function (event) {
        document.querySelectorAll('.suggestions').forEach(function (panel) {
            if (panel.contains(event.target)) return;

            var input = inputFor(panel);
            if (input && input === event.target) return;

            panel.innerHTML = '';
        });
    });

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') return;
        document.querySelectorAll('.suggestions').forEach(function (p) { p.innerHTML = ''; });
    });
})();
