(() => {
  'use strict';

  const INVENTORY_API = '/inventory-api/api';
  const ORDERS_API = '/orders-api/api';
  const REFRESH_INTERVAL_MS = 5000;

  const $ = (selector) => document.querySelector(selector);
  const els = {
    invStatus: $('#inventory-status'),
    ordStatus: $('#orders-status'),
    productsBody: $('#products-body'),
    ordersBody: $('#orders-body'),
    productSelect: $('#order-product'),
    orderQty: $('#order-quantity'),
    orderForm: $('#order-form'),
    orderSubmit: $('#order-submit'),
    productForm: $('#product-form'),
    productSubmit: $('#product-submit'),
    pendingCount: $('#pending-count'),
    retryBtn: $('#retry-btn'),
    refreshProducts: $('#refresh-products'),
    refreshOrders: $('#refresh-orders'),
    dialog: $('#error-dialog'),
    dialogTitle: $('#error-title'),
    dialogMessage: $('#error-message'),
    toasts: $('#toasts'),
  };

  const state = {
    products: [],
    orders: [],
    pending: { count: 0, max: 5 },
    orderInFlight: false,
    clientRequestId: newRequestId(), // rotated only after a definitive answer
    busyOrders: new Set(),
    refreshing: false,
  };

  class ApiError extends Error {
    constructor(status, message) {
      super(message);
      this.name = 'ApiError';
      this.status = status;
    }
  }

  function newRequestId() {
    if (window.crypto && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
    return 'req-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
  }

  // ---------------------------------------------------------------- HTTP
  async function api(path, { method = 'GET', body } = {}) {
    const init = { method, headers: {} };
    if (body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    let res;
    try {
      res = await fetch(path, init);
    } catch (err) {
      throw new ApiError(0, 'Could not reach the front-end server: ' + err.message);
    }

    const text = await res.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); } catch { data = text; }
    }

    if (!res.ok) throw new ApiError(res.status, describeError(data, res));
    return { status: res.status, data };
  }

  function describeError(data, res) {
    if (data && typeof data === 'object') {
      if (data.error) return data.error;
      if (data.errors) {
        return Object.entries(data.errors)
          .map(([field, messages]) => `${field}: ${[].concat(messages).join(', ')}`)
          .join('\n');
      }
      if (data.title) return data.title;
    }
    if (typeof data === 'string' && data) return data;
    return `${res.status} ${res.statusText}`;
  }

  // ---------------------------------------------------------------- UI helpers
  function el(tag, attrs = {}, children = []) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs)) {
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2), value);
      else if (value !== undefined && value !== null && value !== false) node.setAttribute(key, value === true ? '' : value);
    }
    for (const child of [].concat(children)) {
      if (child !== null && child !== undefined) node.append(child);
    }
    return node;
  }

  // Pop-up in the browser + entry in the browser console. The services also log
  // the same refusal in their own terminal.
  function showError(title, message) {
    console.error(`[${title}] ${message}`);
    els.dialogTitle.textContent = title;
    els.dialogMessage.textContent = message;
    if (!els.dialog.open) els.dialog.showModal();
  }

  function toast(message, kind = 'info') {
    console.log(`[${kind}] ${message}`);
    const node = el('div', { class: `toast ${kind}`, text: message });
    els.toasts.append(node);
    setTimeout(() => {
      node.classList.add('fade');
      setTimeout(() => node.remove(), 400);
    }, 4500);
  }

  function productName(productId) {
    const product = state.products.find((p) => p.id === productId);
    return product ? product.name : `Product #${productId}`;
  }

  const formatPrice = (price) => Number(price).toFixed(2);
  const formatDate = (iso) => (iso ? String(iso).replace('T', ' ').slice(0, 19) : '');

  function cancelHint(order) {
    switch (order.status) {
      case 'Confirmed': return 'Cancel this order and return its stock to inventory';
      case 'CancellationPending': return 'Already pending cancellation – the server will refuse a second cancel';
      case 'Cancelled': return 'Already cancelled – the server will refuse this';
      case 'Rejected': return 'Rejected orders cannot be cancelled – the server will refuse this';
      default: return '';
    }
  }

  // ---------------------------------------------------------------- rendering
  function renderProducts() {
    els.productsBody.replaceChildren();
    if (state.products.length === 0) {
      els.productsBody.append(el('tr', {}, el('td', { colspan: 4, class: 'muted', text: 'No products yet — add one below.' })));
    }
    for (const p of state.products) {
      els.productsBody.append(el('tr', {}, [
        el('td', { text: p.id }),
        el('td', { text: p.name }),
        el('td', { text: formatPrice(p.price) }),
        el('td', { class: p.stockQuantity === 0 ? 'stock out' : 'stock', text: p.stockQuantity }),
      ]));
    }

    const current = els.productSelect.value;
    els.productSelect.replaceChildren();
    for (const p of state.products) {
      els.productSelect.append(el('option', { value: p.id, text: `${p.name} — ${p.stockQuantity} in stock` }));
    }
    if (current && state.products.some((p) => String(p.id) === current)) els.productSelect.value = current;
    els.orderSubmit.disabled = state.orderInFlight || state.products.length === 0;
  }

  function renderProductsUnavailable(message) {
    els.productsBody.replaceChildren(el('tr', {}, el('td', { colspan: 4, class: 'muted error', text: `Inventory unavailable: ${message}` })));
  }

  function renderOrders() {
    els.ordersBody.replaceChildren();
    const orders = [...state.orders].sort((a, b) => b.id - a.id);
    if (orders.length === 0) {
      els.ordersBody.append(el('tr', {}, el('td', { colspan: 6, class: 'muted', text: 'No orders yet.' })));
      return;
    }
    for (const o of orders) {
      const busy = state.busyOrders.has(o.id);
      const actions = [
        el('button', {
          class: 'btn small danger', type: 'button', disabled: busy, title: cancelHint(o),
          text: busy ? 'Working…' : 'Cancel', onclick: () => cancelOrder(o),
        }),
      ];
      if (o.status === 'CancellationPending') {
        actions.push(el('button', {
          class: 'btn small', type: 'button', disabled: busy, title: 'Keep the order: revert to Confirmed',
          text: 'Undo', onclick: () => undoCancellation(o),
        }));
      }
      els.ordersBody.append(el('tr', {}, [
        el('td', { text: o.id }),
        el('td', { text: productName(o.productId) }),
        el('td', { text: o.quantity }),
        el('td', {}, el('span', { class: `badge ${String(o.status).toLowerCase()}`, text: o.status })),
        el('td', { text: formatDate(o.dateCreated) }),
        el('td', { class: 'actions' }, actions),
      ]));
    }
  }

  function renderOrdersUnavailable(message) {
    els.ordersBody.replaceChildren(el('tr', {}, el('td', { colspan: 6, class: 'muted error', text: `Orders unavailable: ${message}` })));
  }

  function renderPending() {
    const { count, max } = state.pending;
    els.pendingCount.textContent = `Pending cancellations: ${count} / ${max}`;
    els.pendingCount.className = `pill ${count >= max ? 'down' : count > 0 ? 'warn' : 'neutral'}`;
  }

  function setPill(pill, label, status) {
    pill.textContent = `${label}: ${status.online ? 'online' : 'offline'}`;
    pill.className = `pill ${status.online ? 'ok' : 'down'}`;
    pill.title = status.detail || '';
  }

  // ---------------------------------------------------------------- loaders
  async function loadStatus() {
    try {
      const { data } = await api('/api/status');
      setPill(els.invStatus, 'Inventory', data.inventory);
      setPill(els.ordStatus, 'Orders', data.orders);
    } catch (err) {
      console.warn('Status check failed:', err.message);
    }
  }

  async function loadProducts() {
    try {
      const { data } = await api(`${INVENTORY_API}/products`);
      state.products = data;
      renderProducts();
    } catch (err) {
      renderProductsUnavailable(err.message);
    }
  }

  async function loadOrders() {
    try {
      const [ordersRes, pendingRes] = await Promise.all([
        api(`${ORDERS_API}/orders`),
        api(`${ORDERS_API}/orders/pending-cancellations`),
      ]);
      state.orders = ordersRes.data;
      state.pending = pendingRes.data;
      renderOrders();
      renderPending();
    } catch (err) {
      renderOrdersUnavailable(err.message);
    }
  }

  async function refreshAll() {
    if (state.refreshing) return;
    state.refreshing = true;
    try {
      await loadProducts();               // products first so order rows can show names
      await Promise.all([loadStatus(), loadOrders()]);
    } finally {
      state.refreshing = false;
    }
  }

  // ---------------------------------------------------------------- actions
  async function placeOrder(event) {
    event.preventDefault();
    if (state.orderInFlight) {
      console.warn('Submit ignored: an order request is already in flight.');
      return;
    }

    const productId = Number(els.productSelect.value);
    const quantity = Number(els.orderQty.value);
    if (!productId || !Number.isInteger(quantity) || quantity < 1) {
      showError('Invalid order', 'Choose a product and a whole-number quantity of at least 1.');
      return;
    }

    state.orderInFlight = true;
    els.orderSubmit.disabled = true;
    const originalLabel = els.orderSubmit.textContent;
    els.orderSubmit.textContent = 'Submitting…';

    try {
      const { status, data: order } = await api(`${ORDERS_API}/orders`, {
        method: 'POST',
        body: { productId, quantity, clientRequestId: state.clientRequestId },
      });

      // Definitive answer: the next submission must be a brand-new order.
      state.clientRequestId = newRequestId();

      if (status === 200) {
        toast(`This submission had already been processed as order #${order.id} (${order.status}); no duplicate was created.`, 'warn');
      } else if (order.status === 'Rejected') {
        showError('Order rejected', `Order #${order.id} was rejected: not enough stock for ${quantity} × ${productName(productId)}.`);
      } else {
        toast(`Order #${order.id} confirmed.`, 'ok');
      }
    } catch (err) {
      // 4xx is definitive too; for network errors / 5xx keep the same id so a retry
      // can't turn into a second order.
      if (err instanceof ApiError && err.status >= 400 && err.status < 500) state.clientRequestId = newRequestId();
      showError('Order failed', err.message);
    } finally {
      state.orderInFlight = false;
      els.orderSubmit.textContent = originalLabel;
      els.orderSubmit.disabled = false;
      await refreshAll(); // stock comes back from InventoryService, never computed here
    }
  }

  async function cancelOrder(order) {
    if (state.busyOrders.has(order.id)) return;
    state.busyOrders.add(order.id);
    renderOrders();
    try {
      const { status } = await api(`${ORDERS_API}/orders/${order.id}/cancel`, { method: 'POST' });
      if (status === 202) {
        toast(`Inventory is unavailable — order #${order.id} is now CancellationPending and will be cancelled automatically once InventoryService is back.`, 'warn');
      } else {
        toast(`Order #${order.id} cancelled; ${order.quantity} unit(s) returned to inventory.`, 'ok');
      }
    } catch (err) {
      showError('Cannot cancel order', err.message);
    } finally {
      state.busyOrders.delete(order.id);
      await refreshAll();
    }
  }

  async function undoCancellation(order) {
    if (state.busyOrders.has(order.id)) return;
    state.busyOrders.add(order.id);
    renderOrders();
    try {
      await api(`${ORDERS_API}/orders/${order.id}/undo-cancellation`, { method: 'POST' });
      toast(`Pending cancellation of order #${order.id} undone; the order is Confirmed again.`, 'ok');
    } catch (err) {
      showError('Cannot undo cancellation', err.message);
    } finally {
      state.busyOrders.delete(order.id);
      await refreshAll();
    }
  }

  async function retryPending() {
    els.retryBtn.disabled = true;
    try {
      const { data } = await api(`${ORDERS_API}/orders/retry-cancellations`, { method: 'POST' });
      if (data.skipped) toast('A retry run is already in progress.', 'warn');
      else if (data.pending === 0) toast('No pending cancellations.', 'info');
      else if (data.inventoryUnavailable) toast(`Inventory is still unavailable; ${data.stillPending} cancellation(s) remain pending.`, 'warn');
      else toast(`Retry finished: ${data.cancelled} cancelled, ${data.stillPending} still pending.`, 'ok');
    } catch (err) {
      showError('Retry failed', err.message);
    } finally {
      els.retryBtn.disabled = false;
      await refreshAll();
    }
  }

  async function addProduct(event) {
    event.preventDefault();
    const form = new FormData(els.productForm);
    const body = {
      name: String(form.get('name') || '').trim(),
      price: Number(form.get('price')),
      stockQuantity: Number(form.get('stockQuantity')),
    };
    els.productSubmit.disabled = true;
    try {
      const { data } = await api(`${INVENTORY_API}/products`, { method: 'POST', body });
      toast(`Product "${data.name}" added with ${data.stockQuantity} in stock.`, 'ok');
      els.productForm.reset();
    } catch (err) {
      showError('Cannot add product', err.message);
    } finally {
      els.productSubmit.disabled = false;
      await refreshAll();
    }
  }

  // ---------------------------------------------------------------- wiring
  els.orderForm.addEventListener('submit', placeOrder);
  els.productForm.addEventListener('submit', addProduct);
  els.refreshProducts.addEventListener('click', () => loadProducts());
  els.refreshOrders.addEventListener('click', () => loadOrders());
  els.retryBtn.addEventListener('click', retryPending);

  refreshAll();
  setInterval(refreshAll, REFRESH_INTERVAL_MS); // picks up cancellations completed by the worker
})();
