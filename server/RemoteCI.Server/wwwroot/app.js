async function copyText(value) {
    if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(value); return; }
    // HTTP 局域网部署通常不是安全上下文，保留兼容复制方案。
    const input = document.createElement("textarea");
    input.value = value; input.setAttribute("readonly", ""); input.style.position = "fixed"; input.style.opacity = "0";
    document.body.appendChild(input); input.select();
    const copied = document.execCommand("copy"); input.remove();
    if (!copied) throw new Error("浏览器拒绝复制");
}
function closeMobileSidebar() { document.body.classList.remove("sidebar-open"); }

document.addEventListener("click", async event => {
    const copyButton = event.target.closest("[data-copy-value]");
    if (copyButton) {
        const icon = copyButton.querySelector("i");
        try {
            await copyText(copyButton.dataset.copyValue);
            copyButton.classList.add("copied"); copyButton.setAttribute("aria-label", "配对码已复制"); copyButton.title = "已复制";
            icon?.classList.replace("bi-copy", "bi-check2");
            window.setTimeout(() => { copyButton.classList.remove("copied"); copyButton.setAttribute("aria-label", "复制配对码"); copyButton.title = "复制配对码"; icon?.classList.replace("bi-check2", "bi-copy"); }, 1600);
        } catch { copyButton.setAttribute("aria-label", "复制失败，请手动复制"); copyButton.title = "复制失败，请手动复制"; }
        return;
    }
    if (event.target.closest("[data-sidebar-toggle]")) {
        if (window.matchMedia("(max-width: 820px)").matches) document.body.classList.toggle("sidebar-open");
        else { document.body.classList.toggle("sidebar-collapsed"); localStorage.setItem("remoteci-sidebar-collapsed", document.body.classList.contains("sidebar-collapsed") ? "1" : "0"); }
        return;
    }
    if (event.target.closest("[data-sidebar-backdrop]")) { closeMobileSidebar(); return; }
    if (event.target.closest("[data-theme-toggle]")) {
        const nextTheme = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
        document.documentElement.dataset.theme = nextTheme; localStorage.setItem("remoteci-theme", nextTheme); return;
    }
    if (event.target.closest("[data-page-refresh]")) window.location.reload();
});

const savedTheme = localStorage.getItem("remoteci-theme");
if (savedTheme === "dark") document.documentElement.dataset.theme = "dark";
if (localStorage.getItem("remoteci-sidebar-collapsed") === "1" && !window.matchMedia("(max-width: 820px)").matches) document.body.classList.add("sidebar-collapsed");

const searchInput = document.querySelector("[data-app-search]");
const searchFeedback = document.querySelector("[data-search-feedback]");
const searchEntries = [...document.querySelectorAll("a[data-search-label]")];
searchInput?.addEventListener("input", () => {
    const query = searchInput.value.trim().toLocaleLowerCase("zh-CN");
    if (!query) { searchFeedback?.classList.remove("visible"); return; }
    const match = searchEntries.find(entry => `${entry.textContent} ${entry.dataset.searchLabel}`.toLocaleLowerCase("zh-CN").includes(query));
    if (searchFeedback) { searchFeedback.textContent = match ? `按 Enter 打开：${match.textContent.trim().replace(/\s+/g, " ")}` : "未找到匹配页面或功能"; searchFeedback.classList.add("visible"); }
});
searchInput?.addEventListener("keydown", event => {
    if (event.key !== "Enter") return; event.preventDefault();
    const query = searchInput.value.trim().toLocaleLowerCase("zh-CN");
    const match = searchEntries.find(entry => `${entry.textContent} ${entry.dataset.searchLabel}`.toLocaleLowerCase("zh-CN").includes(query));
    if (match) window.location.href = match.href;
});
window.addEventListener("resize", () => { if (!window.matchMedia("(max-width: 820px)").matches) closeMobileSidebar(); });

document.querySelectorAll("[data-schedule-pull-form]").forEach(form => {
    form.addEventListener("submit", () => {
        const button = form.querySelector("[data-schedule-pull-button]"); const progress = form.querySelector("[data-schedule-pull-progress]");
        if (button) { button.disabled = true; button.textContent = "正在拉取…"; }
        if (progress) progress.hidden = false;
    });
});
