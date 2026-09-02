window.hrPreview = {
    createObjectUrl: function (contentType, bytes) {
        const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
        return URL.createObjectURL(blob);
    },
    revokeObjectUrl: function (url) {
        if (url) URL.revokeObjectURL(url);
    }
};

window.hrPrint = {
    print: function () {
        window.print();
    }
};

window.hrDatePicker = {
    open: function (element) {
        if (!element) return;
        if (typeof element.showPicker === "function") {
            try {
                element.showPicker();
                return;
            } catch (_) {
                // Older browsers may expose showPicker but reject the call.
            }
        }
        element.focus();
        element.click();
    }
};
