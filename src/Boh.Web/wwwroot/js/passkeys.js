// Registering and using passkeys: the browser half of the two WebAuthn ceremonies.
//
// Each one is the same shape. Ask the server for options, hand them to the authenticator,
// post back what it produced, and let the server decide. Both forms this attaches to are
// inert markup until now — neither has a method or an action — so a browser that cannot do
// any of this simply never sees a control that would fail.
//
// Written with async/await rather than the ES5 style app.js keeps: every browser that
// implements WebAuthn has had both for years, and the alternative is four nested callbacks
// per ceremony.
(function () {
    'use strict';

    // No WebAuthn, or no way to turn the server's JSON into the shapes the API wants.
    if (!window.PublicKeyCredential || !navigator.credentials) return;

    // The antiforgery token the layout hands htmx. Read from there rather than rendered a
    // second time, so there is one copy of it in the document.
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

        // Every handler answers a refusal the same way, so one reader covers all of them.
        let reason = 'The server refused that. Try again.';
        try {
            const problem = await response.json();
            if (problem && problem.error) reason = problem.error;
        } catch (e) { /* not JSON — the generic message is all there is to say */ }

        throw new Error(reason);
    }

    // ---- converting between the wire format and the API's buffers ----------
    //
    // WebAuthn options travel as JSON with base64url in place of the byte arrays, and
    // browsers have parseCreationOptionsFromJSON/parseRequestOptionsFromJSON to turn them
    // back. Older ones that support WebAuthn but not those helpers get the same job done
    // below rather than being shut out.

    function toBuffer(value) {
        const padded = value.replace(/-/g, '+').replace(/_/g, '/');
        const raw = atob(padded + '==='.slice((padded.length + 3) % 4));
        const bytes = new Uint8Array(raw.length);

        for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes.buffer;
    }

    function toBase64Url(buffer) {
        if (!buffer) return undefined;

        const bytes = new Uint8Array(buffer);
        let raw = '';

        for (let i = 0; i < bytes.length; i++) raw += String.fromCharCode(bytes[i]);
        return btoa(raw).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    function withDecodedIds(list) {
        return (list || []).map(function (descriptor) {
            return Object.assign({}, descriptor, { id: toBuffer(descriptor.id) });
        });
    }

    function creationOptions(json) {
        if (PublicKeyCredential.parseCreationOptionsFromJSON) {
            return PublicKeyCredential.parseCreationOptionsFromJSON(json);
        }

        return Object.assign({}, json, {
            challenge: toBuffer(json.challenge),
            user: Object.assign({}, json.user, { id: toBuffer(json.user.id) }),
            excludeCredentials: withDecodedIds(json.excludeCredentials)
        });
    }

    function requestOptions(json) {
        if (PublicKeyCredential.parseRequestOptionsFromJSON) {
            return PublicKeyCredential.parseRequestOptionsFromJSON(json);
        }

        return Object.assign({}, json, {
            challenge: toBuffer(json.challenge),
            allowCredentials: withDecodedIds(json.allowCredentials)
        });
    }

    /// Serializing a credential normally means its own toJSON. Some password managers
    /// implement that incorrectly and throw, so the fields are assembled by hand instead
    /// of the registration failing for a reason the person cannot act on.
    function credentialJson(credential) {
        try {
            return JSON.stringify(credential);
        } catch (e) {
            const response = credential.response;

            return JSON.stringify({
                id: credential.id,
                rawId: toBase64Url(credential.rawId),
                type: credential.type,
                authenticatorAttachment: credential.authenticatorAttachment,
                clientExtensionResults: credential.getClientExtensionResults(),
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
            });
        }
    }

    // ---- shared plumbing for the two forms ---------------------------------

    const problem = document.getElementById('passkey-error');

    function report(message) {
        if (!problem) return;

        problem.textContent = message;
        problem.hidden = !message;
    }

    /// Runs one ceremony with the button disabled, so a second click cannot start a second
    /// one while an authenticator prompt is already open.
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
                // NotAllowedError is the browser's word for "cancelled, or nothing
                // matched", which is a choice rather than a fault and needs no notice.
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
            const options = await (await post(adding.dataset.passkeyOptions)).json();

            const credential = await navigator.credentials.create({
                publicKey: creationOptions(options)
            });

            if (!credential) throw new Error('The browser produced no passkey.');

            await post(adding.dataset.passkeyRegister, JSON.stringify({
                name: (adding.querySelector('[name="name"]') || {}).value || '',
                credential: JSON.parse(credentialJson(credential))
            }));

            // Reloaded rather than patched in: the new row, and the message the server left
            // in TempData, both come from the page it renders next.
            window.location.reload();
        });
    }

    // ---- signing in with one, on the login page ----------------------------

    const signingIn = document.getElementById('passkey-signin');

    if (signingIn) {
        wire(signingIn, async function () {
            const options = await (await post(signingIn.dataset.passkeyOptions)).json();

            const credential = await navigator.credentials.get({
                publicKey: requestOptions(options)
            });

            if (!credential) throw new Error('No passkey was offered.');

            const response = await post(signingIn.dataset.passkeyAssert, credentialJson(credential));
            const result = await response.json();

            window.location.assign(result.redirect || '/');
        });
    }
})();
