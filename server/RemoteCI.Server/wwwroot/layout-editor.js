(() => {
    const root = document.querySelector("[data-layout-page]");
    const pageKey = root?.dataset.layoutPage;
    const editButton = document.querySelector("[data-layout-edit]");
    const toolbar = document.querySelector("[data-layout-toolbar]");
    const tokenForm = document.querySelector("[data-layout-antiforgery]");
    if (!root || !pageKey || !editButton || !toolbar || !tokenForm) return;

    const groups = [...root.querySelectorAll("[data-layout-group]")];
    editButton.hidden = groups.length === 0;
    if (groups.length === 0) return;

    const status = toolbar.querySelector("[data-layout-status]");
    const saveButton = toolbar.querySelector("[data-layout-save]");
    const cancelButton = toolbar.querySelector("[data-layout-cancel]");
    const resetButton = toolbar.querySelector("[data-layout-reset]");
    const token = tokenForm.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
    const endpoint = tokenForm.action;
    let layoutLoaded = false;
    let editing = false;
    let editSnapshot = null;
    let draggedCard = null;
    editButton.disabled = true;

    function cardsIn(group) {
        return [...group.querySelectorAll(":scope > [data-layout-card]")];
    }

    function normalizedSpan(card, value) {
        const span = Number(value ?? card.dataset.layoutDefaultSpan ?? 1);
        return Number.isInteger(span) && span >= 1 && span <= 3 ? span : 1;
    }

    function setCardSpan(card, span) {
        const normalized = normalizedSpan(card, span);
        card.dataset.cardSpan = String(normalized);
        const selector = card.querySelector("[data-layout-span]");
        if (selector) selector.value = String(normalized);
    }

    function collectDocument() {
        const items = [];
        groups.forEach(group => {
            cardsIn(group).forEach((card, order) => items.push({
                cardId: card.dataset.layoutCard,
                groupId: group.dataset.layoutGroup,
                order,
                span: normalizedSpan(card, card.dataset.cardSpan),
            }));
        });
        return { version: 1, items };
    }

    const defaultDocument = collectDocument();

    function applyGroupLayout(group, items) {
        const currentCards = cardsIn(group);
        const byId = new Map(currentCards.map(card => [card.dataset.layoutCard, card]));
        const configured = items
            .filter(item => item.groupId === group.dataset.layoutGroup && byId.has(item.cardId))
            .sort((left, right) => left.order - right.order);
        const configuredIds = new Set(configured.map(item => item.cardId));
        const orderedCards = configured.map(item => byId.get(item.cardId));
        orderedCards.push(...currentCards.filter(card => !configuredIds.has(card.dataset.layoutCard)));
        orderedCards.forEach(card => group.append(card));

        const configuredById = new Map(configured.map(item => [item.cardId, item]));
        orderedCards.forEach(card => setCardSpan(
            card,
            configuredById.get(card.dataset.layoutCard)?.span ?? card.dataset.layoutDefaultSpan));
    }

    function applyDocument(document) {
        const items = Array.isArray(document?.items) ? document.items : [];
        groups.forEach(group => applyGroupLayout(group, items));
    }

    async function getLayout() {
        const response = await fetch(`${endpoint}?handler=Get&pageKey=${encodeURIComponent(pageKey)}`, {
            headers: { Accept: "application/json" },
        });
        if (!response.ok) throw new Error("\u65e0\u6cd5\u8bfb\u53d6\u5361\u7247\u5e03\u5c40");
        return response.json();
    }

    async function postLayout(handler, values = {}) {
        const data = new FormData();
        data.set("__RequestVerificationToken", token);
        data.set("pageKey", pageKey);
        Object.entries(values).forEach(([key, value]) => data.set(key, value));
        const response = await fetch(`${endpoint}?handler=${handler}`, {
            method: "POST",
            body: data,
            headers: { Accept: "application/json", "X-Requested-With": "XMLHttpRequest" },
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(payload.error || "\u5361\u7247\u5e03\u5c40\u64cd\u4f5c\u5931\u8d25");
        return payload;
    }

    function moveCard(card, offset) {
        const group = card.closest("[data-layout-group]");
        const cards = cardsIn(group);
        const index = cards.indexOf(card);
        const target = cards[index + offset];
        if (!target) return;
        if (offset < 0) group.insertBefore(card, target);
        else group.insertBefore(target, card);
        card.querySelector("[data-layout-drag]")?.focus();
    }

    function createCardControls(card) {
        if (card.querySelector(":scope > [data-layout-controls]")) return;
        const controls = document.createElement("div");
        controls.className = "layout-card-controls";
        controls.dataset.layoutControls = "";
        controls.innerHTML = `
            <button type="button" class="layout-drag-handle" data-layout-drag aria-label="\u62d6\u52a8\u6216\u4f7f\u7528\u65b9\u5411\u952e\u8c03\u6574\u5361\u7247\u987a\u5e8f" title="\u62d6\u52a8\u8c03\u6574\u987a\u5e8f"><i class="bi bi-grip-vertical" aria-hidden="true"></i></button>
            <button type="button" class="ghost" data-layout-move="-1" aria-label="\u5411\u524d\u79fb\u52a8\u5361\u7247"><i class="bi bi-arrow-left" aria-hidden="true"></i></button>
            <button type="button" class="ghost" data-layout-move="1" aria-label="\u5411\u540e\u79fb\u52a8\u5361\u7247"><i class="bi bi-arrow-right" aria-hidden="true"></i></button>
            <label>\u5bbd\u5ea6<select data-layout-span aria-label="\u5361\u7247\u5bbd\u5ea6"><option value="1">1 \u5217</option><option value="2">2 \u5217</option><option value="3">\u6574\u884c</option></select></label>`;
        card.prepend(controls);
        const dragHandle = controls.querySelector("[data-layout-drag]");
        dragHandle.draggable = true;
        controls.querySelectorAll("[data-layout-move]").forEach(button =>
            button.addEventListener("click", () => moveCard(card, Number(button.dataset.layoutMove))));
        controls.querySelector("[data-layout-span]").addEventListener("change", event =>
            setCardSpan(card, event.target.value));
        controls.querySelector("[data-layout-drag]").addEventListener("keydown", event => {
            if (event.key === "ArrowLeft" || event.key === "ArrowUp") moveCard(card, -1);
            else if (event.key === "ArrowRight" || event.key === "ArrowDown") moveCard(card, 1);
            else return;
            event.preventDefault();
        });
    }

    function removeCardControls(card) {
        card.querySelector(":scope > [data-layout-controls]")?.remove();
        card.classList.remove("layout-card-dragging");
    }

    function enterEditMode() {
        if (editing || !layoutLoaded) return;
        editing = true;
        editSnapshot = collectDocument();
        status.textContent = "拖动卡片调整顺序，并选择卡片宽度。";
        root.classList.add("layout-editing");
        toolbar.hidden = false;
        editButton.setAttribute("aria-pressed", "true");
        groups.forEach(group => cardsIn(group).forEach(createCardControls));
    }

    function exitEditMode() {
        editing = false;
        draggedCard?.classList.remove("layout-card-dragging");
        draggedCard = null;
        editSnapshot = null;
        root.classList.remove("layout-editing");
        toolbar.hidden = true;
        editButton.setAttribute("aria-pressed", "false");
        saveButton.disabled = false;
        cancelButton.disabled = false;
        resetButton.disabled = false;
        groups.forEach(group => cardsIn(group).forEach(removeCardControls));
    }

    function setBusy(busy, message) {
        saveButton.disabled = busy;
        cancelButton.disabled = busy;
        resetButton.disabled = busy;
        if (message) status.textContent = message;
    }

    groups.forEach(group => {
        group.addEventListener("dragstart", event => {
            const handle = event.target.closest("[data-layout-drag]");
            const card = handle?.closest("[data-layout-card]");
            if (!editing || !card) return event.preventDefault();
            draggedCard = card;
            card.classList.add("layout-card-dragging");
            event.dataTransfer.effectAllowed = "move";
        });
        group.addEventListener("dragover", event => {
            if (!editing || !draggedCard || draggedCard.parentElement !== group) return;
            const target = event.target.closest("[data-layout-card]");
            if (!target || target === draggedCard) return;
            event.preventDefault();
            const bounds = target.getBoundingClientRect();
            const after = event.clientY > bounds.top + bounds.height / 2 ||
                (Math.abs(event.clientY - (bounds.top + bounds.height / 2)) < bounds.height / 3 && event.clientX > bounds.left + bounds.width / 2);
            group.insertBefore(draggedCard, after ? target.nextSibling : target);
        });
        group.addEventListener("drop", event => event.preventDefault());
        group.addEventListener("dragend", () => {
            draggedCard?.classList.remove("layout-card-dragging");
            draggedCard = null;
        });
    });

    editButton.addEventListener("click", enterEditMode);
    cancelButton.addEventListener("click", () => {
        if (editSnapshot) applyDocument(editSnapshot);
        exitEditMode();
    });
    saveButton.addEventListener("click", async () => {
        setBusy(true, "\u6b63\u5728\u4fdd\u5b58\u6392\u7248\u2026");
        try {
            const document = collectDocument();
            applyDocument(await postLayout("Save", { layoutJson: JSON.stringify(document) }));
            exitEditMode();
        } catch (error) {
            setBusy(false, error instanceof Error ? error.message : "\u4fdd\u5b58\u6392\u7248\u5931\u8d25");
        }
    });
    resetButton.addEventListener("click", async () => {
        if (!window.confirm("\u786e\u5b9a\u6062\u590d\u6b64\u9875\u9762\u7684\u9ed8\u8ba4\u5361\u7247\u5e03\u5c40\uff1f")) return;
        setBusy(true, "\u6b63\u5728\u6062\u590d\u9ed8\u8ba4\u5e03\u5c40\u2026");
        try {
            await postLayout("Reset");
            applyDocument(defaultDocument);
            exitEditMode();
        } catch (error) {
            setBusy(false, error instanceof Error ? error.message : "\u6062\u590d\u9ed8\u8ba4\u5e03\u5c40\u5931\u8d25");
        }
    });

    getLayout().then(applyDocument).catch(error => {
        console.warn("Card layout could not be loaded", error);
    }).finally(() => {
        layoutLoaded = true;
        editButton.disabled = false;
        root.classList.add("layout-ready");
    });
})();
