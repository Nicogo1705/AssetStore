// Copyright (c) Stride contributors (https://stride3d.net) - MIT license
window.assetStoreEnv = {
    hostname: function () { return location.hostname; },
    href: function () { return location.href; },
    copy: function (text) {
        if (navigator.clipboard) { return navigator.clipboard.writeText(text); }
        return Promise.resolve();
    }
};
