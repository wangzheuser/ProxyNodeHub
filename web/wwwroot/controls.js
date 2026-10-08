"use strict";

function createWorkspaceControls() {
  const selects = new WeakMap();
  let closeSelect = null;
  let positionSelect = null;
  let finishConfirmation = null;
  const dialog = document.getElementById("confirmation-dialog");
  const accept = document.getElementById("confirmation-accept");
  const cancel = document.getElementById("confirmation-cancel");

  function dismiss() {
    closeSelect?.();
    finishConfirmation?.(false);
  }

  function confirm(message) {
    dismiss();
    const previous = document.activeElement;
    document.getElementById("confirmation-message").textContent = message;
    return new Promise(resolve => {
      finishConfirmation = result => {
        finishConfirmation = null;
        dialog.close();
        if (previous?.isConnected && !previous.matches(":disabled")) previous.focus({ preventScroll: true });
        resolve(result);
      };
      dialog.showModal();
      cancel.focus();
    });
  }
  accept.addEventListener("click", () => finishConfirmation?.(true));
  cancel.addEventListener("click", () => finishConfirmation?.(false));
  dialog.addEventListener("cancel", event => { event.preventDefault(); finishConfirmation?.(false); });

  function enhance(select) {
    const wrapper = document.createElement("span");
    wrapper.className = "choice-control";
    const trigger = document.createElement("button");
    trigger.type = "button";
    trigger.id = `${select.id}-trigger`;
    trigger.className = "choice-trigger";
    trigger.setAttribute("role", "combobox");
    trigger.setAttribute("aria-haspopup", "listbox");
    trigger.setAttribute("aria-expanded", "false");
    const label = document.createElement("span");
    label.className = "choice-value";
    trigger.append(label);
    // Keep the select as the single form value owner; only its presentation changes.
    const labels = [...select.labels];
    const caption = labels.map(item => [...item.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join("").trim()).join(" ");
    labels.forEach(item => { item.htmlFor = trigger.id; });
    trigger.setAttribute("aria-label", caption || select.getAttribute("aria-label") || "选择选项");
    select.before(wrapper);
    wrapper.append(select, trigger);
    select.hidden = true;
    const list = document.createElement("div");
    list.id = `${select.id}-options`;
    list.className = "choice-options";
    list.setAttribute("role", "listbox");
    list.setAttribute("aria-label", trigger.getAttribute("aria-label"));
    list.hidden = true;
    wrapper.append(list);
    trigger.setAttribute("aria-controls", list.id);
    let active = -1;
    let prefix = "";
    let lastKeyAt = 0;
    let openedOptions = "";
    let openedValue = "";
    const optionSignature = () => JSON.stringify([...select.options].map(option => [option.value, option.textContent, option.disabled, option.hidden]));
    const available = () => [...select.options].map((option, index) => ({ option, index })).filter(({ option }) => !option.disabled && !option.hidden);
    const close = () => {
      list.hidden = true;
      trigger.setAttribute("aria-expanded", "false");
      trigger.removeAttribute("aria-activedescendant");
      if (closeSelect === close) { closeSelect = null; positionSelect = null; }
    };
    function highlight(index) {
      active = index;
      for (const option of list.children) option.classList.toggle("is-active", Number(option.dataset.index) === active);
      const option = document.getElementById(`${list.id}-${active}`);
      if (option) {
        trigger.setAttribute("aria-activedescendant", option.id);
        // Scroll only the option list, not its off-screen form ancestors.
        if (option.offsetTop < list.scrollTop) list.scrollTop = option.offsetTop;
        else if (option.offsetTop + option.offsetHeight > list.scrollTop + list.clientHeight)
          list.scrollTop = option.offsetTop + option.offsetHeight - list.clientHeight;
      }
    }
    function sync() {
      if (!list.hidden && (openedOptions !== optionSignature() || openedValue !== select.value)) close();
      const value = select.selectedOptions[0]?.textContent || "请选择";
      if (label.textContent !== value) label.textContent = value;
      const disabled = select.matches(":disabled");
      if (trigger.disabled !== disabled) trigger.disabled = disabled;
      if (trigger.disabled) close();
    }
    function position() {
      const rect = trigger.getBoundingClientRect();
      if (rect.bottom < 0 || rect.top > window.innerHeight) { close(); return; }
      const below = window.innerHeight - rect.bottom - 12;
      const above = rect.top - 12;
      const upward = below < 180 && above > below;
      list.style.width = `${Math.min(Math.max(rect.width, 180), window.innerWidth - 24)}px`;
      list.style.maxHeight = `${Math.max(44, Math.min(300, upward ? above : below))}px`;
      list.style.left = `${Math.max(12, Math.min(rect.left, window.innerWidth - list.offsetWidth - 12))}px`;
      list.style.top = `${upward ? rect.top - list.offsetHeight - 4 : rect.bottom + 4}px`;
    }
    function open(index = select.selectedIndex) {
      if (select.matches(":disabled") || !available().length) return;
      closeSelect?.();
      sync();
      list.replaceChildren(...available().map(({ option, index: optionIndex }) => {
        const item = document.createElement("div");
        item.id = `${list.id}-${optionIndex}`;
        item.dataset.index = optionIndex;
        item.setAttribute("role", "option");
        item.setAttribute("aria-selected", String(optionIndex === select.selectedIndex));
        item.textContent = option.textContent;
        return item;
      }));
      openedOptions = optionSignature();
      openedValue = select.value;
      list.hidden = false;
      trigger.setAttribute("aria-expanded", "true");
      closeSelect = close;
      positionSelect = position;
      position();
      highlight(available().some(item => item.index === index) ? index : available()[0].index);
    }
    function commit() {
      if (active < 0 || select.matches(":disabled")) return;
      const changed = select.selectedIndex !== active;
      select.selectedIndex = active;
      close();
      sync();
      if (changed) {
        select.dispatchEvent(new Event("input", { bubbles: true }));
        select.dispatchEvent(new Event("change", { bubbles: true }));
      }
    }
    trigger.addEventListener("click", () => list.hidden ? open() : close());
    trigger.addEventListener("blur", close);
    trigger.addEventListener("keydown", event => {
      const options = available();
      if (!options.length) return;
      if (event.key === "Escape") { if (!list.hidden) { event.preventDefault(); event.stopPropagation(); close(); } return; }
      if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
        event.preventDefault();
        if (list.hidden) open();
        const position = options.findIndex(item => item.index === active);
        const next = event.key === "Home" ? 0 : event.key === "End" ? options.length - 1
          : Math.max(0, Math.min(options.length - 1, position + (event.key === "ArrowDown" ? 1 : -1)));
        highlight(options[next].index);
      } else if (["Enter", " "].includes(event.key)) {
        event.preventDefault();
        if (list.hidden) open(); else commit();
      } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
        event.preventDefault();
        prefix = Date.now() - lastKeyAt < 700 ? prefix + event.key : event.key;
        lastKeyAt = Date.now();
        const match = options.find(({ option }) => option.textContent.toLocaleLowerCase().startsWith(prefix.toLocaleLowerCase()));
        if (list.hidden) open();
        if (match) highlight(match.index);
      }
    });
    list.addEventListener("pointerdown", event => event.preventDefault());
    list.addEventListener("click", event => {
      // Options live inside the form label; do not activate its trigger again.
      event.preventDefault();
      const option = event.target.closest('[role="option"]');
      if (!option) return;
      active = Number(option.dataset.index);
      commit();
      trigger.focus({ preventScroll: true });
    });
    select.addEventListener("change", sync);
    selects.set(select, { sync, trigger });
    sync();
  }

  function refresh() {
    document.querySelectorAll("select").forEach(select => {
      if (!selects.has(select)) enhance(select);
      else selects.get(select).sync();
    });
    document.querySelectorAll("form").forEach(form => { form.noValidate = true; });
    document.querySelectorAll('[aria-invalid="true"]').forEach(control => {
      if (control.matches(":disabled") || control.validity?.valid) clearError(control);
    });
  }
  document.addEventListener("pointerdown", event => { if (!event.target.closest(".choice-control")) closeSelect?.(); });
  document.addEventListener("scroll", event => { if (!event.target.closest?.(".choice-options")) positionSelect?.(); }, true);
  window.addEventListener("resize", () => closeSelect?.());
  // Fieldset disabling also disables the corresponding custom trigger.
  new MutationObserver(refresh).observe(document.body, { subtree: true, attributes: true, attributeFilter: ["disabled"] });

  function clearError(control) {
    const error = document.getElementById(`${control.id}-error`);
    if (!error?.classList.contains("control-error")) return;
    error.remove();
    control.removeAttribute("aria-invalid");
    const ids = (control.getAttribute("aria-describedby") || "").split(" ").filter(id => id !== error.id);
    if (ids.length) control.setAttribute("aria-describedby", ids.join(" "));
    else control.removeAttribute("aria-describedby");
  }
  document.addEventListener("invalid", event => {
    event.preventDefault();
    const control = event.target;
    let error = document.getElementById(`${control.id}-error`);
    if (!error) {
      error = document.createElement("span");
      error.id = `${control.id}-error`;
      error.className = "control-error";
      error.setAttribute("role", "alert");
      (control.closest(".choice-control") || control).after(error);
    }
    error.textContent = control.validationMessage;
    control.setAttribute("aria-invalid", "true");
    const ids = new Set((control.getAttribute("aria-describedby") || "").split(" ").filter(Boolean));
    ids.add(error.id);
    control.setAttribute("aria-describedby", [...ids].join(" "));
  }, true);
  document.addEventListener("input", event => { if (event.target.validity?.valid) clearError(event.target); });
  document.addEventListener("submit", event => {
    if (event.target.checkValidity()) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const first = [...event.target.elements].find(control => control.willValidate && !control.validity.valid);
    (selects.get(first)?.trigger || first)?.focus();
  }, true);
  refresh();
  return { refresh, confirm, dismiss };
}
