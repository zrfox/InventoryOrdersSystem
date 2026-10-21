'use strict';

const POLL_INTERVAL_MS = 4000;
const JSON_HEADERS = { 'Content-Type': 'application/json' };

const $ = (id) => document.getElementById(id);
const ui = {
  inventoryStatus: $('inventory-status'),
  ordersStatus: $('orders-status'),
  banner: $('banner'),
  productsBody: $('products-body'),
  ordersBody: $('orders-body'),
  productForm: $('product-form'),
  productName: $('product-name-input'),
  productPrice: $('product-price-input'),
  productStock: $('product-stock-input'),
  productSubmit: $('product-submit-button'),
  form: $('order-form'),
  productSelect: $('product-select'),
  quantity: $('quantity-input'),
  submit: $('submit-button'),
  toast: $('toast'),
  popup: $('popup'),
  popupTitle: $('popup-title'),
  popupMessage: $('popup-message'),
};

const state = {
  products: [],             // exactly what InventoryService returned last time
  inventoryUp: null,        // null = not known yet
  orders: [],
  ordersUp: null,
  productNames: new Map(),  // names only (never stock), so orders stay readable while Inventory is down
  busyOrders: new Set(),    // order ids with a cancel/undo request in flight
  submitting: false,
  addingProduct: false,
  pendingSubmission: null,  // { key, fingerprint } of a submission whose outcome isn't known yet
  stockEdit: null,          // the one open stock editor: { productId, expected, row, input, warning, saveButton, saving }
};

// ---------- talking to the server ----------

class ApiError extends Error {
  constructor(message, status, body) {
    super(message);
    this.status = status; // 0 = no response at all
    this.body = body;
  }
}

async function api(path, options = {}) {
  let response;
  try {
    response = await fetch(path, { cache: 'no-store', ...options });
  } catch {
    throw new ApiError('The front-end server could not be reached.', 0, null);
  }

  const text = await response.text();
  let body = null;
  if (text) {
    try { body = JSON.parse(text); } catch { body = text; }
  }

  if (!response.ok) throw new ApiError(describeError(body, response.status), response.status, body);
  return { status: response.status, body };
}

function describeError(body, status) {
  if (typeof body === 'string' && body.length <= 300 && !body.trimStart().startsWith('<')) return body;
  if (body && typeof body === 'object') {
    if (typeof body.error === 'string') return body.error;
    if (typeof body.message === 'string') return body.message;
    if (body.errors && typeof body.errors === 'object') {
      const messages = Object.values(body.errors).flat().filter((m) => typeof m === 'string');
      if (messages.length) return messages.join(' ');
    }
    if (typeof body.detail === 'string') return body.detail;
    if (typeof body.title === 'string') return body.title;
  }
  return `Request failed (HTTP ${status}).`;
}

function newIdempotencyKey() {
  if (window.crypto && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

// Runs fn at most once at a time; a call made while it is running triggers one more run afterwards,
// so a refresh requested after an action never ends up showing data from before the action.
function coalesced(fn) {
  let running = null;
  let again = false;
  return function run() {
    if (running) { again = true; return running; }
    running = (async () => {
      try {
        do { again = false; await fn(); } while (again);
      } finally {
        running = null;
      }
    })();
    return running;
  };
}

const refreshProducts = coalesced(async () => {
  try {
    const { body } = await api('/api/products');
    state.products = Array.isArray(body) ? body : [];
    state.inventoryUp = true;
    for (const p of state.products) state.productNames.set(p.id, p.name);
  } catch {
    state.inventoryUp = false;
    state.products = []; // never keep showing stock numbers we can't confirm
  }
  render();
});

const refreshOrders = coalesced(async () => {
  try {
    const { body } = await api('/api/orders');
    state.orders = Array.isArray(body) ? body : [];
    state.ordersUp = true;
  } catch {
    state.ordersUp = false;
  }
  render();
});

function refreshAll() {
  return Promise.all([refreshProducts(), refreshOrders()]);
}

// ---------- products ----------

ui.productForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (state.addingProduct) return; // ignore extra clicks while one is in flight

  const name = ui.productName.value.trim();
  const price = Number(ui.productPrice.value);
  const stockQuantity = Number(ui.productStock.value);
  if (!name || name.length > 100) {
    showPopup('Product not added', 'Enter a name of up to 100 characters.');
    return;
  }
  if (!(price > 0)) {
    showPopup('Product not added', 'Enter a price greater than zero.');
    return;
  }
  if (!Number.isInteger(stockQuantity) || stockQuantity < 0) {
    showPopup('Product not added', 'Initial stock must be a whole number of 0 or more.');
    return;
  }

  state.addingProduct = true;
  render();

  try {
    const { body: product } = await api('/api/products', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({ name, price, stockQuantity }),
    });
    showToast(`Product "${product.name}" added with ${product.stockQuantity} in stock.`);
    ui.productForm.reset();
  } catch (err) {
    reportError('Product not added', err);
  } finally {
    state.addingProduct = false;
    render();
    refreshProducts();
  }
});

function startStockEdit(product) {
  const input = el('input', {
    type: 'number', min: 0, step: 1, className: 'stock-input', value: String(product.stockQuantity),
  });
  const warning = el('div', { className: 'hint warn', hidden: true });
  const saveButton = el('button', { type: 'button', textContent: 'Save', onclick: () => saveStockEdit() });
  const cancelButton = el('button', {
    type: 'button', className: 'secondary', textContent: 'Cancel', onclick: () => { closeStockEdit(); render(); },
  });
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') { event.preventDefault(); saveStockEdit(); }
    if (event.key === 'Escape') { closeStockEdit(); render(); }
  });

  // This row is reused as-is on every re-render, so polling never wipes what is being typed.
  const row = el('tr', { className: 'editing' },
    el('td', { textContent: product.id }),
    el('td', { textContent: product.name }),
    el('td', { className: 'num', textContent: money.format(product.price) }),
    el('td', { className: 'num' }, input, warning),
    el('td', {}, saveButton, ' ', cancelButton));

  // "expected" is the stock the user was looking at; the server refuses the save if it has moved.
  state.stockEdit = { productId: product.id, expected: product.stockQuantity, row, input, warning, saveButton, saving: false };
  render();
  input.focus();
  input.select();
}

function closeStockEdit() {
  state.stockEdit = null;
}

async function saveStockEdit() {
  const edit = state.stockEdit;
  if (!edit || edit.saving) return; // ignore double clicks / Enter + click

  const stockQuantity = Number(edit.input.value);
  if (!Number.isInteger(stockQuantity) || stockQuantity < 0) {
    showPopup('Stock not saved', 'Enter a whole number of 0 or more.');
    return;
  }

  edit.saving = true;
  edit.input.disabled = true;
  edit.saveButton.disabled = true;
  edit.saveButton.textContent = 'Saving…';
  render();

  try {
    await api(`/api/products/${edit.productId}/stock`, {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({ expectedStockQuantity: edit.expected, stockQuantity }),
    });
    showToast(`Stock of ${state.productNames.get(edit.productId) || `product ${edit.productId}`} set to ${stockQuantity}.`);
    closeStockEdit();
  } catch (err) {
    reportError('Stock not saved', err);
    if (err.status === 409) {
      // The stock moved under us; the server said no and told us the current value.
      // Close the editor so the next edit starts from the fresh number.
      closeStockEdit();
    } else {
      edit.saving = false;
      edit.input.disabled = false;
      edit.saveButton.disabled = false;
      edit.saveButton.textContent = 'Save';
    }
  } finally {
    render();
    refreshProducts();
  }
}

// ---------- orders ----------

ui.form.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (state.submitting) return; // ignore extra clicks / Enter presses while one is in flight

  const productId = Number(ui.productSelect.value);
  const quantity = Number(ui.quantity.value);
  if (!Number.isInteger(productId) || productId < 1 || !Number.isInteger(quantity) || quantity < 1) {
    showPopup('Order not placed', 'Choose a product and a whole-number quantity of at least 1.');
    return;
  }

  // Same key for the same submission (including a retry after a network error), so the
  // server can recognise a duplicate. A new key only once the server has given a clear answer.
  const fingerprint = `${productId}:${quantity}`;
  if (!state.pendingSubmission || state.pendingSubmission.fingerprint !== fingerprint) {
    state.pendingSubmission = { key: newIdempotencyKey(), fingerprint };
  }
  const { key } = state.pendingSubmission;

  state.submitting = true;
  render();

  try {
    const { body: order } = await api('/api/orders', {
      method: 'POST',
      headers: { ...JSON_HEADERS, 'Idempotency-Key': key },
      body: JSON.stringify({ productId, quantity }),
    });
    state.pendingSubmission = null;

    if (order && order.status === 'Rejected') {
      showPopup('Order rejected', `Order #${order.id} was rejected: there is not enough stock.`);
    } else if (order) {
      showToast(`Order #${order.id} ${String(order.status).toLowerCase()}.`);
    }
  } catch (err) {
    // 4xx = the server clearly said no, nothing was created. Anything else (no response, 5xx)
    // is unclear, so the key is kept and a retry can't create a second order.
    if (err.status >= 400 && err.status < 500) state.pendingSubmission = null;
    reportError('Order not placed', err);
  } finally {
    state.submitting = false;
    render();
    refreshAll();
  }
});

function cancelOrder(id) {
  return orderAction(id, `/api/orders/${id}/cancel`, `Could not cancel order #${id}`, (status) => {
    if (status === 202) {
      showPopup('Cancellation pending',
        `InventoryService is not available, so order #${id} is now CancellationPending. ` +
        'It will be cancelled automatically, and its stock returned, once InventoryService is running again. ' +
        'Until then you can undo the cancellation.');
    } else {
      showToast(`Order #${id} cancelled. Stock returned to inventory.`);
    }
  });
}

function undoCancellation(id) {
  return orderAction(id, `/api/orders/${id}/cancel/undo`, `Could not undo the cancellation of order #${id}`, () => {
    showToast(`Cancellation of order #${id} undone. The order is confirmed again.`);
  });
}

async function orderAction(id, path, errorTitle, onSuccess) {
  if (state.busyOrders.has(id)) return; // ignore double clicks
  state.busyOrders.add(id);
  render();
  try {
    const { status } = await api(path, { method: 'POST' });
    onSuccess(status);
  } catch (err) {
    reportError(errorTitle, err);
  } finally {
    state.busyOrders.delete(id);
    render();
    refreshAll();
  }
}

// ---------- rendering ----------

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  Object.assign(node, props);
  node.append(...children);
  return node;
}

function messageRow(columns, text) {
  return el('tr', {}, el('td', { colSpan: columns, className: 'empty', textContent: text }));
}

const money = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

function formatDate(value) {
  if (!value) return '';
  // The API sends UTC times; add the "Z" if the serializer left it off.
  const iso = /(Z|[+-]\d\d:\d\d)$/.test(value) ? value : `${value}Z`;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

let lastRendered = '';

function render() {
  const snapshot = JSON.stringify([
    state.products, state.orders, state.inventoryUp, state.ordersUp,
    [...state.busyOrders], state.submitting, state.addingProduct,
    state.stockEdit ? [state.stockEdit.productId, state.stockEdit.saving] : null,
  ]);
  if (snapshot === lastRendered) return; // nothing changed: leave the page alone
  lastRendered = snapshot;

  renderStatus();
  renderProducts();
  renderProductSelect();
  renderOrders();

  ui.productSubmit.disabled = state.addingProduct || !state.inventoryUp;
  ui.productSubmit.textContent = state.addingProduct ? 'Adding…' : 'Add product';

  ui.submit.disabled = state.submitting || !state.inventoryUp || !state.ordersUp || state.products.length === 0;
  ui.submit.textContent = state.submitting ? 'Submitting…' : 'Submit order';
}

function renderStatus() {
  setPill(ui.inventoryStatus, 'InventoryService', state.inventoryUp);
  setPill(ui.ordersStatus, 'OrdersService', state.ordersUp);

  const problems = [];
  if (state.inventoryUp === false) {
    problems.push('InventoryService is unavailable: stock cannot be shown or edited, and products and orders cannot be added. ' +
      'Cancelling a confirmed order will put it in CancellationPending until InventoryService is back.');
  }
  if (state.ordersUp === false) {
    problems.push('OrdersService is unavailable: orders cannot be placed, cancelled or refreshed right now.');
  }
  ui.banner.hidden = problems.length === 0;
  ui.banner.textContent = problems.join(' ');
}

function setPill(node, name, up) {
  node.textContent = `${name}: ${up === null ? 'checking…' : up ? 'running' : 'unavailable'}`;
  node.className = `pill ${up === null ? '' : up ? 'up' : 'down'}`;
}

function renderProducts() {
  const rows = [];
  if (state.inventoryUp === null) {
    rows.push(messageRow(5, 'Loading…'));
  } else if (state.inventoryUp === false) {
    rows.push(messageRow(5, 'Stock is unavailable while InventoryService cannot be reached.'));
  } else if (state.products.length === 0) {
    rows.push(messageRow(5, 'No products yet. Add one with the form below.'));
  } else {
    const edit = state.stockEdit;
    if (edit && !state.products.some((p) => p.id === edit.productId)) {
      closeStockEdit(); // the product being edited no longer exists
    }
    for (const p of state.products) rows.push(productRow(p));
  }
  ui.productsBody.replaceChildren(...rows);
}

function productRow(product) {
  const edit = state.stockEdit;
  if (edit && edit.productId === product.id) {
    // Live staleness warning: the server will refuse the save if this is showing.
    const moved = product.stockQuantity !== edit.expected;
    edit.warning.hidden = !moved;
    edit.warning.textContent = moved
      ? `Changed to ${product.stockQuantity} since you started editing (you saw ${edit.expected}). Saving will be refused; cancel and edit again.`
      : '';
    return edit.row;
  }

  return el('tr', {},
    el('td', { textContent: product.id }),
    el('td', { textContent: product.name }),
    el('td', { className: 'num', textContent: money.format(product.price) }),
    el('td', { className: product.stockQuantity === 0 ? 'num out' : 'num', textContent: product.stockQuantity }),
    el('td', {}, el('button', {
      type: 'button',
      textContent: 'Edit stock',
      disabled: edit !== null, // one editor at a time
      onclick: () => startStockEdit(product),
    })));
}

function renderProductSelect() {
  const previous = ui.productSelect.value;
  const options = state.products.map((p) =>
    el('option', { value: String(p.id), textContent: `${p.name} (${p.stockQuantity} in stock)` }));
  ui.productSelect.replaceChildren(...options);
  if (state.products.some((p) => String(p.id) === previous)) ui.productSelect.value = previous;
}

const STATUS_LABELS = {
  Confirmed: 'Confirmed',
  Rejected: 'Rejected',
  Cancelled: 'Cancelled',
  CancellationPending: 'Cancellation pending',
  Pending: 'Pending',
};

function renderOrders() {
  const rows = [];
  if (state.ordersUp === null) {
    rows.push(messageRow(6, 'Loading…'));
  } else if (state.orders.length === 0) {
    rows.push(messageRow(6, state.ordersUp ? 'No orders yet.' : 'Orders are unavailable while OrdersService cannot be reached.'));
  } else {
    const newestFirst = [...state.orders].sort((a, b) => b.id - a.id);
    for (const order of newestFirst) rows.push(orderRow(order));
  }
  ui.ordersBody.replaceChildren(...rows);
}

function orderRow(order) {
  const busy = state.busyOrders.has(order.id);
  const name = state.productNames.get(order.productId) || `Product ${order.productId}`;

  const statusCell = el('td', {},
    el('span', { className: `badge ${order.status}`, textContent: STATUS_LABELS[order.status] || order.status }));
  if (order.status === 'CancellationPending') {
    statusCell.append(el('div', {
      className: 'hint',
      textContent: `Waiting for InventoryService · ${order.cancellationAttempts} attempt(s) so far`,
    }));
  }

  const actions = el('td', {});
  if (order.status === 'Confirmed') {
    actions.append(el('button', {
      type: 'button',
      className: 'danger',
      textContent: busy ? 'Cancelling…' : 'Cancel order',
      disabled: busy || !state.ordersUp,
      onclick: () => cancelOrder(order.id),
    }));
  } else if (order.status === 'CancellationPending') {
    actions.append(el('button', {
      type: 'button',
      textContent: busy ? 'Undoing…' : 'Undo cancellation',
      disabled: busy || !state.ordersUp || order.cancellationInDoubt,
      title: order.cancellationInDoubt
        ? 'A cancellation request may already have reached InventoryService, so it can no longer be undone.'
        : '',
      onclick: () => undoCancellation(order.id),
    }));
  } else {
    actions.append('—');
  }

  return el('tr', {},
    el('td', { textContent: order.id }),
    el('td', { textContent: name }),
    el('td', { className: 'num', textContent: order.quantity }),
    el('td', { textContent: formatDate(order.dateCreated) }),
    statusCell,
    actions);
}

// ---------- pop-up and toast ----------

function showPopup(title, message) {
  if (ui.popup.open) ui.popup.close();
  ui.popupTitle.textContent = title;
  ui.popupMessage.textContent = message;
  ui.popup.showModal();
}

function reportError(title, err) {
  console.error(`${title}: ${err.message}`);
  showPopup(title, err.message);
}

let toastTimer = null;
function showToast(message) {
  ui.toast.textContent = message;
  ui.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { ui.toast.hidden = true; }, 4000);
}

// ---------- start ----------

render();
refreshAll();
setInterval(refreshAll, POLL_INTERVAL_MS);