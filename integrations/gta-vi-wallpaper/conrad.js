(function () {
  'use strict';
  var base = 'http://127.0.0.1:19187', token = (window.CONRAD_CONNECTION || {}).token;
  var $ = function (id) { return document.getElementById('sensor-' + id); };
  var root = document.getElementById('conrad'), state = null, gpu = null;
  var online = false, pending = false, paused = false, timer = null, generation = 0;
  function fmt(value, unit) { return typeof value === 'number' && Number.isFinite(value) ? Math.round(value) + unit : '—'; }
  function layout() { window.dispatchEvent(new Event('conradlayout')); }
  function error(message) { $('error').textContent = message || ''; $('error').hidden = !message; layout(); }
  async function request(path, body) {
    var controller = new AbortController();
    var timeout = setTimeout(function () { controller.abort(); }, body ? 90000 : 5000);
    try {
      var response = await fetch(base + path, {
        method: body ? 'POST' : 'GET', cache: 'no-store', signal: controller.signal,
        headers: Object.assign({'X-Conrad-Token': token}, body ? {'Content-Type': 'application/json'} : {}),
        body: body ? JSON.stringify(body) : undefined
      });
      if (!response.ok) {
        var failure = await response.json().catch(function () { return {}; });
        throw new Error(failure.error || (response.status === 401 ? 'Connexion Conrad à réassocier.' : 'Conrad indisponible.'));
      }
      return await response.json();
    } finally { clearTimeout(timeout); }
  }
  function updateControls() {
    var busy = pending || !!(state && state.busy);
    $('heatwave').disabled = !online || busy;
    $('heatwave').setAttribute('aria-pressed', String(!!(state && state.heatwaveActive)));
    $('controls').disabled = !online || busy || !gpu || !gpu.available;
    $('manual').disabled = !gpu || !gpu.fanControlSupported;
    $('fan-slider').disabled = !gpu || !gpu.fanControlSupported || !$('manual').checked;
    $('thermal-slider').disabled = !gpu || !gpu.thermalControlSupported;
    $('apply').disabled = !gpu || !(gpu.fanControlSupported || gpu.thermalControlSupported);
    $('fan-target').textContent = $('manual').checked ? $('fan-slider').value + ' %' : 'AUTO';
    $('thermal-target').textContent = gpu && gpu.thermalControlSupported ? $('thermal-slider').value + '°' : '—';
    if (busy) $('status').textContent = 'APPLICATION…';
  }
  function render(next) {
    state = next;
    var s = next && next.snapshot;
    if (s && Date.now() - Date.parse(s.fetchedAt) > 15000) s = null;
    root.classList.toggle('online', online && !!s);
    var hot = s && (s.cpuPackageCelsius >= 90 || s.gpuCelsius >= 84);
    root.classList.toggle('hot', !!hot);
    $('status').textContent = !online ? 'CONRAD HORS LIGNE' : !s ? 'CAPTEURS INDISPONIBLES' : hot ? 'TEMPÉRATURE ÉLEVÉE' : next.heatwaveActive ? 'CANICULE ACTIVE' : 'EN DIRECT';
    $('cpu').textContent = fmt(s && s.cpuPackageCelsius, '°');
    $('gpu').textContent = fmt(s && s.gpuCelsius, '°');
    $('cpu-load').textContent = fmt(s && s.cpuLoadPercent, '%');
    $('gpu-load').textContent = fmt(s && s.gpuLoadPercent, '%');
    $('fan').textContent = fmt(s && s.gpuFanPercent, '%');
    $('power').textContent = fmt(s && s.gpuPowerWatts, ' W');
    $('hottest').textContent = fmt(s && s.cpuHottestCoreCelsius, '°');
    $('cpu-bar').style.width = Math.max(0, Math.min(100, s && s.cpuPackageCelsius || 0)) + '%';
    $('gpu-bar').style.width = Math.max(0, Math.min(100, s && s.gpuCelsius || 0)) + '%';
    var cores = s && s.cpuCores || [];
    for (var i = 0; i < 6; i++) $('cores').children[i].lastChild.textContent = fmt(cores[i] && cores[i].celsius, '°');
    updateControls();
  }
  async function poll() {
    clearTimeout(timer);
    if (paused || document.hidden) return;
    var current = generation;
    try {
      var next = await request('/state');
      if (current !== generation) return;
      online = true;
      render(next);
    } catch (_) {
      if (current !== generation) return;
      online = false; gpu = null; render(null);
    } finally {
      if (current === generation && !paused && !document.hidden) timer = setTimeout(poll, online ? 2000 : 5000);
    }
  }
  async function loadGpu() {
    gpu = null; updateControls();
    $('control-status').textContent = 'CONNEXION…';
    try {
      gpu = await request('/gpu');
      $('control-status').textContent = gpu.available ? 'NVIDIA' : 'INDISPONIBLE';
      $('manual').checked = !gpu.fanAuto;
      $('fan-slider').min = gpu.fanMinPercent;
      $('fan-slider').max = gpu.fanMaxPercent;
      $('fan-slider').value = gpu.fanPercent;
      $('thermal-slider').min = gpu.thermalMinCelsius;
      $('thermal-slider').max = gpu.thermalMaxCelsius;
      $('thermal-slider').value = gpu.thermalLimitCelsius;
      if (!gpu.available) error(gpu.status);
    } catch (e) { $('control-status').textContent = 'INDISPONIBLE'; error(e.message); }
    updateControls();
  }
  async function command(body) {
    if (pending || !online || state && state.busy) return;
    pending = true; error(null); updateControls();
    try {
      var next = await request('/command', body);
      render(next);
      if (!$('cooling').hidden) await loadGpu();
    } catch (e) { error(e.name === 'AbortError' ? 'Confirmation Windows en attente. Vérifiez Conrad.' : e.message); }
    finally { pending = false; updateControls(); generation++; poll(); }
  }
  ['details', 'cooling'].forEach(function (name) {
    $(name + '-button').addEventListener('click', function () {
      var open = $(name).hidden;
      ['details', 'cooling'].forEach(function (other) {
        $(other).hidden = other !== name || !open;
        $(other + '-button').setAttribute('aria-expanded', String(other === name && open));
      });
      error(null); layout();
      if (open) window.dispatchEvent(new CustomEvent('dashboarddrawer', {detail:'conrad'}));
      if (name === 'cooling' && open) loadGpu();
    });
  });
  $('heatwave').addEventListener('click', function () { command({action:'heatwave', enabled: !state.heatwaveActive}); });
  $('manual').addEventListener('change', updateControls);
  $('fan-slider').addEventListener('input', updateControls);
  $('thermal-slider').addEventListener('input', updateControls);
  $('apply').addEventListener('click', function () {
    command({action:'apply', settings:{fanManual:$('manual').checked, fanPercent:Number($('fan-slider').value), thermalLimitCelsius:Number($('thermal-slider').value)}});
  });
  for (var i = 0; i < 6; i++) {
    var cell = document.createElement('div'), label = document.createElement('span'), value = document.createElement('strong');
    label.textContent = 'CŒUR ' + (i + 1); value.textContent = '—'; cell.append(label, value); $('cores').appendChild(cell);
  }
  function resume() { generation++; clearTimeout(timer); if (!paused && !document.hidden) poll(); }
  window.addEventListener('dashboarddrawer', function (event) {
    if (event.detail === 'conrad') return;
    ['details', 'cooling'].forEach(function (name) { $(name).hidden = true; $(name + '-button').setAttribute('aria-expanded', 'false'); });
    error(null); layout();
  });
  window.addEventListener('conradpause', function (event) { paused = event.detail; resume(); });
  document.addEventListener('visibilitychange', resume);
  render(null); layout();
  if (token) poll(); else { $('status').textContent = 'CONRAD À ASSOCIER'; }
})();
