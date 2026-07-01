// Copyright (c) <YEAR> <COPYRIGHT HOLDER> - MIT license
window.assetStoreEnv = {
    hostname: function () { return location.hostname; },
    copy: function (text) {
        if (navigator.clipboard) { return navigator.clipboard.writeText(text); }
        return Promise.resolve();
    },
    // Best-effort client OS detection for the download page: windows | macos | linux | unknown.
    os: function () {
        var p = (navigator.userAgentData && navigator.userAgentData.platform)
            || navigator.platform || navigator.userAgent || '';
        p = p.toLowerCase();
        if (p.indexOf('win') !== -1) { return 'windows'; }
        if (p.indexOf('mac') !== -1 || p.indexOf('iphone') !== -1 || p.indexOf('ipad') !== -1) { return 'macos'; }
        if (p.indexOf('linux') !== -1 || p.indexOf('android') !== -1) { return 'linux'; }
        return 'unknown';
    }
};

// Secure-at-rest storage for the GitHub token.
// - sessionStorage: the (encrypted) token is wiped when the tab/browser closes.
// - AES-GCM via WebCrypto with a NON-EXTRACTABLE key kept in IndexedDB: what sits in
//   storage is ciphertext, and the key's raw bytes cannot be exported (defeats passive
//   snooping / a localStorage dump). It cannot stop an *active* XSS on the page.
window.assetStoreSecureToken = (function () {
    const SS_KEY = 'assetstore.ghtoken.enc';
    const LEGACY_KEY = 'assetstore.ghtoken';
    const DB_NAME = 'assetstore';
    const STORE = 'keys';
    const KEY_ID = 'token-key';

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = () => req.result.createObjectStore(STORE);
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }
    function idb(db, mode, fn) {
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, mode);
            const req = fn(tx.objectStore(STORE));
            tx.oncomplete = () => resolve(req && req.result);
            tx.onerror = () => reject(tx.error);
        });
    }
    async function getKey(create) {
        const db = await openDb();
        let key = await idb(db, 'readonly', s => s.get(KEY_ID));
        if (!key && create) {
            key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
            await idb(db, 'readwrite', s => s.put(key, KEY_ID));
        }
        return key;
    }
    const b64 = {
        enc: buf => btoa(String.fromCharCode(...new Uint8Array(buf))),
        dec: s => Uint8Array.from(atob(s), c => c.charCodeAt(0))
    };

    return {
        save: async function (token) {
            try { localStorage.removeItem(LEGACY_KEY); } catch (e) { /* ignore */ }
            try {
                const key = await getKey(true);
                const iv = crypto.getRandomValues(new Uint8Array(12));
                const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode(token));
                sessionStorage.setItem(SS_KEY, b64.enc(iv) + ':' + b64.enc(ct));
            } catch (e) {
                // Never silently fall back to plaintext: drop it instead.
                try { sessionStorage.removeItem(SS_KEY); } catch (e2) { /* ignore */ }
            }
        },
        load: async function () {
            try {
                const blob = sessionStorage.getItem(SS_KEY);
                if (!blob) { return null; }
                const key = await getKey(false);
                if (!key) { return null; }
                const [ivB, ctB] = blob.split(':');
                const pt = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: b64.dec(ivB) }, key, b64.dec(ctB));
                return new TextDecoder().decode(pt);
            } catch (e) {
                return null;
            }
        },
        clear: async function () {
            try { sessionStorage.removeItem(SS_KEY); } catch (e) { /* ignore */ }
            try { localStorage.removeItem(LEGACY_KEY); } catch (e) { /* ignore */ }
            try { const db = await openDb(); await idb(db, 'readwrite', s => s.delete(KEY_ID)); } catch (e) { /* ignore */ }
        }
    };
})();
