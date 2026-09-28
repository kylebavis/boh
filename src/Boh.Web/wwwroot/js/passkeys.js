// Browser half of the WebAuthn ceremonies: fetch options, run the authenticator, post the
// result. The forms have no method or action, so nothing shows where this can't run.
(function () {
    'use strict';

    if (!window.PublicKeyCredential || !navigator.credentials
        || !PublicKeyCredential.parseCreationOptionsFromJSON || !Uint8Array.prototype.toBase64) return;

    // The antiforgery token from the layout's hx-headers.
    function verificationToken() {
        try {
            return JSON.parse(document.body.getAttribute('hx-headers') || '{}').RequestVerificationToken || '';
        } catch (e) {
            return '';
        }
    }

    async function post(url, body) {
        const headers = { 'RequestVerificationToken': verificationToken() };
        if (body !== undefined) headers['Content-Type'] = 'application/json';

        const response = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: headers,
            body: body
        });

        if (response.ok) return response;

        let reason = 'The server refused that. Try again.';
        try {
            const problem = await response.json();
            if (problem && problem.error) reason = problem.error;
        } catch (e) { /* not JSON — the generic message is all there is to say */ }

        throw new Error(reason);
    }

    /// Reads a JSON reply. A non-JSON one usually means a redirect to sign-in after the
    /// session ended; say that, and log the detail.
    async function readJson(response) {
        const body = await response.text();

        try {
            return JSON.parse(body);
        } catch (e) {
            if (window.console) {
                console.error('boh: expected JSON from ' + response.url + ', got '
                    + response.status + ' ' + (response.headers.get('content-type') || 'no content type')
                    + (response.redirected ? ' (redirected)' : ''), body.slice(0, 500));
            }

            if (response.redirected || /^\s*</.test(body)) {
                throw new Error('That request ended up somewhere else — your session may have expired. Reload the page and try again.');
            }

            throw new Error('The server sent a reply this page could not read. See the browser console for what arrived.');
        }
    }

    // ---- converting the API's buffers back to the wire format --------------

    function toBase64Url(buffer) {
        if (!buffer) return undefined;
        return new Uint8Array(buffer).toBase64({ alphabet: 'base64url', omitPadding: true });
    }

    /// The credential as a plain object. toJSON can't be trusted: password managers replace
    /// navigator.credentials and may omit fields (1Password drops clientExtensionResults) or throw.
    function serialize(credential) {
        let json = null;

        try {
            json = JSON.parse(JSON.stringify(credential));
        } catch (e) {
        }

        if (!isCredentialJson(json)) json = assemble(credential);

        // Required by the server and the likeliest field to be missing.
        if (!json.clientExtensionResults || typeof json.clientExtensionResults !== 'object') {
            json.clientExtensionResults = extensionResults(credential);
        }

        return json;
    }

    /// Whether this is real credential JSON, not an empty or {}-buffered stand-in.
    function isCredentialJson(json) {
        return !!json
            && typeof json.id === 'string'
            && typeof json.rawId === 'string'
            && !!json.response
            && typeof json.response.clientDataJSON === 'string';
    }

    function extensionResults(credential) {
        try {
            return credential.getClientExtensionResults() || {};
        } catch (e) {
            return {};
        }
    }

    /// Every field the server reads, taken by hand; fields of the other ceremony drop out.
    function assemble(credential) {
        const response = credential.response;

        return {
            id: credential.id,
            rawId: toBase64Url(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment,
            response: {
                clientDataJSON: toBase64Url(response.clientDataJSON),
                attestationObject: toBase64Url(response.attestationObject),
                authenticatorData: toBase64Url(
                    response.authenticatorData || (response.getAuthenticatorData && response.getAuthenticatorData())),
                publicKey: toBase64Url(response.getPublicKey && response.getPublicKey()),
                publicKeyAlgorithm: response.getPublicKeyAlgorithm && response.getPublicKeyAlgorithm(),
                transports: response.getTransports ? response.getTransports() : undefined,
                signature: toBase64Url(response.signature),
                userHandle: toBase64Url(response.userHandle)
            }
        };
    }

    // ---- shared plumbing for the two forms ---------------------------------

    const problem = document.getElementById('passkey-error');

    function report(message) {
        if (!problem) return;

        problem.textContent = message;
        problem.hidden = !message;
    }

    /// Disables the button so a second click can't start a second ceremony.
    function wire(form, run) {
        const button = form.querySelector('button');

        form.hidden = false;
        if (button) button.hidden = false;

        form.addEventListener('submit', async function (event) {
            event.preventDefault();
            report('');

            if (button) button.disabled = true;

            try {
                await run();
            } catch (error) {
                // NotAllowedError is cancellation or no match, not a fault.
                if (error.name !== 'NotAllowedError' && error.name !== 'AbortError') {
                    report(error.message || 'That did not work.');
                }
            } finally {
                if (button) button.disabled = false;
            }
        });
    }

    // ---- registering a passkey, on the account page ------------------------

    const adding = document.getElementById('passkey-form');

    if (adding) {
        wire(adding, async function () {
            const options = await readJson(await post(adding.dataset.passkeyOptions));

            const credential = await navigator.credentials.create({
                publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options)
            });

            if (!credential) throw new Error('The browser produced no passkey.');

            await post(adding.dataset.passkeyRegister, JSON.stringify({
                name: (adding.querySelector('[name="name"]') || {}).value || '',
                credential: serialize(credential)
            }));

            // Reload: the new row and TempData message come from the server.
            window.location.reload();
        });
    }

    // ---- signing in with one, on the login page ----------------------------

    const signingIn = document.getElementById('passkey-signin');

    if (signingIn) {
        wire(signingIn, async function () {
            const options = await readJson(await post(signingIn.dataset.passkeyOptions));

            const credential = await navigator.credentials.get({
                publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options)
            });

            if (!credential) throw new Error('No passkey was offered.');

            // returnUrl in the body: in the query string, cookie auth answers with a redirect.
            const response = await post(signingIn.dataset.passkeyAssert, JSON.stringify({
                returnUrl: signingIn.dataset.passkeyReturn || '/',
                credential: serialize(credential)
            }));
            const result = await readJson(response);

            window.location.assign(result.redirect || '/');
        });
    }
})();
