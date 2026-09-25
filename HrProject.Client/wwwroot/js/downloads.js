window.hrPreview = {
    createObjectUrl: function (contentType, bytes) {
        const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
        return URL.createObjectURL(blob);
    },
    revokeObjectUrl: function (url) {
        if (url) URL.revokeObjectURL(url);
    }
};

window.hrScroll = {
    toBottom: function (elementId, smooth) {
        const element = document.getElementById(elementId);
        if (!element) return;
        requestAnimationFrame(() => requestAnimationFrame(() => {
            element.scrollTo({
                top: element.scrollHeight,
                behavior: smooth ? "smooth" : "auto"
            });
        }));
    }
};

window.hrImageViewer = {
    viewers: new Map(),
    initialize: function (viewportId, imageId, scaleId) {
        this.destroy(viewportId);
        const viewport = document.getElementById(viewportId);
        const image = document.getElementById(imageId);
        const scaleLabel = document.getElementById(scaleId);
        if (!viewport || !image) return;

        const state = {
            viewport, image, scaleLabel,
            scale: 1, fitScale: 1, x: 0, y: 0,
            dragging: false, startX: 0, startY: 0, originX: 0, originY: 0
        };
        const update = () => {
            image.style.transform = `translate(-50%, -50%) translate(${state.x}px, ${state.y}px) scale(${state.scale})`;
            if (scaleLabel) scaleLabel.textContent = `${Math.round(state.scale * 100)}%`;
            viewport.classList.toggle("is-zoomed", state.scale > state.fitScale + 0.01);
        };
        const calculateFit = () => {
            if (!image.naturalWidth || !image.naturalHeight) return 1;
            return Math.min(
                Math.max(100, viewport.clientWidth - 32) / image.naturalWidth,
                Math.max(100, viewport.clientHeight - 32) / image.naturalHeight,
                1);
        };
        const fit = () => {
            state.fitScale = calculateFit();
            state.scale = state.fitScale;
            state.x = 0;
            state.y = 0;
            update();
        };
        const setScale = value => {
            state.scale = Math.min(8, Math.max(0.1, value));
            update();
        };
        state.onWheel = event => {
            event.preventDefault();
            setScale(state.scale * (event.deltaY < 0 ? 1.15 : 1 / 1.15));
        };
        state.onPointerDown = event => {
            if (event.button !== 0) return;
            state.dragging = true;
            state.startX = event.clientX;
            state.startY = event.clientY;
            state.originX = state.x;
            state.originY = state.y;
            viewport.setPointerCapture(event.pointerId);
        };
        state.onPointerMove = event => {
            if (!state.dragging) return;
            state.x = state.originX + event.clientX - state.startX;
            state.y = state.originY + event.clientY - state.startY;
            update();
        };
        state.onPointerUp = event => {
            state.dragging = false;
            if (viewport.hasPointerCapture(event.pointerId)) viewport.releasePointerCapture(event.pointerId);
        };
        state.onDoubleClick = () => {
            if (Math.abs(state.scale - 1) < 0.01) fit();
            else { state.scale = 1; state.x = 0; state.y = 0; update(); }
        };
        state.onResize = () => fit();
        state.fit = fit;
        state.update = update;
        viewport.addEventListener("wheel", state.onWheel, { passive: false });
        viewport.addEventListener("pointerdown", state.onPointerDown);
        viewport.addEventListener("pointermove", state.onPointerMove);
        viewport.addEventListener("pointerup", state.onPointerUp);
        viewport.addEventListener("pointercancel", state.onPointerUp);
        viewport.addEventListener("dblclick", state.onDoubleClick);
        window.addEventListener("resize", state.onResize);
        this.viewers.set(viewportId, state);
        fit();
    },
    zoom: function (viewportId, factor) {
        const state = this.viewers.get(viewportId);
        if (!state) return;
        state.scale = Math.min(8, Math.max(0.1, state.scale * factor));
        state.update();
    },
    fit: function (viewportId) {
        const state = this.viewers.get(viewportId);
        if (state) state.fit();
    },
    actualSize: function (viewportId) {
        const state = this.viewers.get(viewportId);
        if (!state) return;
        state.scale = 1;
        state.x = 0;
        state.y = 0;
        state.update();
    },
    destroy: function (viewportId) {
        const state = this.viewers.get(viewportId);
        if (!state) return;
        state.viewport.removeEventListener("wheel", state.onWheel);
        state.viewport.removeEventListener("pointerdown", state.onPointerDown);
        state.viewport.removeEventListener("pointermove", state.onPointerMove);
        state.viewport.removeEventListener("pointerup", state.onPointerUp);
        state.viewport.removeEventListener("pointercancel", state.onPointerUp);
        state.viewport.removeEventListener("dblclick", state.onDoubleClick);
        window.removeEventListener("resize", state.onResize);
        this.viewers.delete(viewportId);
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

window.hrDownload = {
    downloadText: function (fileName, content, contentType) {
        const blob = new Blob([content], { type: contentType || "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName || "download.txt";
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    downloadBytes: function (fileName, contentType, bytes) {
        const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName || "attachment";
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }
};

window.hrCommentPaste = {
    handlers: new Map(),
    register: function (elementId, dotnet) {
        const element = document.getElementById(elementId);
        if (!element || this.handlers.has(elementId)) return;
        const handler = async function (event) {
            const items = Array.from(event.clipboardData?.items || []);
            const imageItem = items.find(item => item.kind === "file" && item.type.startsWith("image/"));
            if (!imageItem) return;
            event.preventDefault();
            const file = imageItem.getAsFile();
            if (!file) return;
            const reader = new FileReader();
            reader.onload = async function () {
                const value = String(reader.result || "");
                const comma = value.indexOf(",");
                if (comma < 0) return;
                const extension = file.type === "image/jpeg" ? "jpg" :
                    file.type === "image/webp" ? "webp" : "png";
                const name = file.name && file.name !== "image.png"
                    ? file.name
                    : `capture-${new Date().toISOString().replace(/[:.]/g, "-")}.${extension}`;
                await dotnet.invokeMethodAsync("ReceivePastedImage", name, file.type, value.substring(comma + 1));
            };
            reader.readAsDataURL(file);
        };
        element.addEventListener("paste", handler);
        this.handlers.set(elementId, { element, handler });
    },
    unregister: function (elementId) {
        const registration = this.handlers.get(elementId);
        if (!registration) return;
        registration.element.removeEventListener("paste", registration.handler);
        this.handlers.delete(elementId);
    }
};
