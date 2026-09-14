(() => {
  const ICE = { iceServers: [{ urls: "stun:stun.l.google.com:19302" }] };
  const $ = (id) => document.getElementById(id);
  const palette = ["#316ac5", "#2ea043", "#c42b1c", "#6b2fa0", "#c96b00", "#0b7a8f", "#1d4a99", "#a32020"];

  const state = {
    id: "",
    callsign: "",
    net: "Lobby",
    mic: true,
    cam: true,
    members: [],
    pcs: new Map(),
    localStream: null,
    talking: false
  };

  let hub;

  function nickColor(name) {
    let h = 0;
    for (const c of name) h = (h * 33 + c.charCodeAt(0)) >>> 0;
    return palette[h % palette.length];
  }

  function initials(name) {
    const p = (name || "?").trim().split(/\s+/);
    return ((p[0]?.[0] || "?") + (p[1]?.[0] || "")).toUpperCase();
  }

  function stamp() {
    return new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }

  function escapeHtml(s) {
    return (s || "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  function logLine(kind, nick, text) {
    const el = document.createElement("div");
    el.className = "line" + (kind === "sys" ? " sys" : kind === "whisper" ? " wh" : "");
    const n = nick ? `<span class="n" style="color:${nickColor(nick)}">${escapeHtml(nick)}:</span>` : "";
    el.innerHTML = `<span class="t">${stamp()}</span> ${n}${escapeHtml(text)}`;
    $("log").appendChild(el);
    $("log").scrollTop = $("log").scrollHeight;
  }

  function renderNets(census) {
    const ul = $("nets");
    ul.innerHTML = "";
    const nets = Object.keys(census || {}).length
      ? Object.keys(census)
      : ["Lobby", "Area 850", "War Room", "Night Watch", "Off-Comms"];
    for (const n of nets) {
      const li = document.createElement("li");
      if (n === state.net) li.classList.add("active");
      li.innerHTML = `<span>▾ ${escapeHtml(n)}</span><span class="meta">${census?.[n] ?? ""}</span>`;
      li.onclick = () => switchNet(n);
      ul.appendChild(li);
    }
  }

  function renderRoster() {
    const ul = $("roster");
    ul.innerHTML = "";
    for (const m of state.members) {
      const li = document.createElement("li");
      if (m.talking) li.classList.add("talking");
      if (!m.mic) li.classList.add("off");
      const col = nickColor(m.callsign);
      li.innerHTML = `<span class="dpic" style="background:${col}">${escapeHtml(initials(m.callsign))}</span>
        <span class="status-dot"></span>
        <span>${escapeHtml(m.callsign)}${m.id === state.id ? " (you)" : ""}</span>`;
      li.title = m.id === state.id ? "That's you" : "Click to whisper";
      if (m.id !== state.id) {
        li.onclick = () => {
          $("msg").value = `/w ${m.callsign} `;
          $("msg").focus();
        };
      }
      ul.appendChild(li);
    }
  }

  function attachVideo(video, stream) {
    video.srcObject = stream;
    video.muted = video.hasAttribute("muted") || video.parentElement?.classList.contains("self");
    video.playsInline = true;
    video.autoplay = true;
    const play = () => video.play().catch(() => {});
    video.onloadedmetadata = play;
    play();
  }

  function ensureCam(id, callsign, self) {
    let box = document.getElementById("cam-" + id);
    if (box) return box;
    box = document.createElement("div");
    box.className = "cam" + (self ? " self" : "");
    box.id = "cam-" + id;
    const col = nickColor(callsign);
    box.innerHTML = `<div class="ph" style="background:${col}">${escapeHtml(initials(callsign))}</div>
      <video ${self ? "muted" : ""} playsinline autoplay></video>
      <div class="who">${escapeHtml(callsign)}</div>`;
    $("cams").appendChild(box);
    return box;
  }

  function dropCam(id) {
    document.getElementById("cam-" + id)?.remove();
  }

  function showCam(id, on) {
    document.getElementById("cam-" + id)?.classList.toggle("cam-off", !on);
  }

  async function media() {
    if (state.localStream) return state.localStream;
    const audio = {
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true
    };
    try {
      state.localStream = await navigator.mediaDevices.getUserMedia({
        audio,
        video: { width: { ideal: 320 }, height: { ideal: 240 }, frameRate: { ideal: 15, max: 24 } }
      });
    } catch (e) {
      logLine("sys", "", "Webcam didn't start (" + (e.message || e) + "). Trying microphone only…");
      state.localStream = await navigator.mediaDevices.getUserMedia({ audio, video: false });
      state.cam = false;
    }
    for (const t of state.localStream.getAudioTracks()) t.enabled = state.mic;
    for (const t of state.localStream.getVideoTracks()) t.enabled = state.cam;
    const self = ensureCam("local", state.callsign + " (you)", true);
    attachVideo(self.querySelector("video"), state.localStream);
    showCam("local", state.cam && state.localStream.getVideoTracks().length > 0);
    if (state.localStream.getAudioTracks().length) watchTalking(state.localStream);
    return state.localStream;
  }

  function watchTalking(stream) {
    const ctx = new AudioContext();
    const src = ctx.createMediaStreamSource(stream);
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 512;
    src.connect(analyser);
    const data = new Uint8Array(analyser.fftSize);
    const tick = () => {
      analyser.getByteTimeDomainData(data);
      let sum = 0;
      for (const v of data) {
        const x = (v - 128) / 128;
        sum += x * x;
      }
      const rms = Math.sqrt(sum / data.length);
      const on = state.mic && rms > 0.045;
      if (on !== state.talking) {
        state.talking = on;
        document.getElementById("cam-local")?.classList.toggle("talk", on);
        hub?.invoke("Talking", on);
      }
      requestAnimationFrame(tick);
    };
    tick();
  }

  async function callPeer(peerId, initiator) {
    if (state.pcs.has(peerId) || peerId === state.id) return;
    const stream = await media();
    const pc = new RTCPeerConnection(ICE);
    state.pcs.set(peerId, pc);
    for (const track of stream.getTracks()) pc.addTrack(track, stream);
    pc.onicecandidate = (e) => {
      if (e.candidate) hub.invoke("Signal", peerId, "ice", JSON.stringify(e.candidate));
    };
    pc.ontrack = (e) => {
      const m = state.members.find((x) => x.id === peerId);
      const box = ensureCam(peerId, m?.callsign || "peer", false);
      const remote = e.streams[0] || new MediaStream([e.track]);
      attachVideo(box.querySelector("video"), remote);
      showCam(peerId, m ? m.cam !== false : true);
    };
    pc.onconnectionstatechange = () => {
      if (["failed", "closed", "disconnected"].includes(pc.connectionState)) hangup(peerId);
    };
    if (initiator) {
      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      await hub.invoke("Signal", peerId, "offer", JSON.stringify(pc.localDescription));
    }
  }

  function hangup(peerId) {
    const pc = state.pcs.get(peerId);
    if (pc) {
      try { pc.close(); } catch {}
      state.pcs.delete(peerId);
    }
    dropCam(peerId);
  }

  async function onSignal(fromId, kind, payload) {
    let pc = state.pcs.get(fromId);
    if (kind === "offer") {
      if (!pc) await callPeer(fromId, false);
      pc = state.pcs.get(fromId);
      await pc.setRemoteDescription(JSON.parse(payload));
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      await hub.invoke("Signal", fromId, "answer", JSON.stringify(pc.localDescription));
      return;
    }
    if (!pc) return;
    if (kind === "answer") await pc.setRemoteDescription(JSON.parse(payload));
    if (kind === "ice") {
      try { await pc.addIceCandidate(JSON.parse(payload)); } catch {}
    }
  }

  async function enter(net, others) {
    state.net = net;
    $("net-name").textContent = net;
    $("you-line").textContent = "Signed in as " + state.callsign;
    renderRoster();
    renderNets(window._census || {});
    await media();
    $("cam-btn").classList.toggle("on", state.cam);
    $("cam-btn").classList.toggle("off", !state.cam);
    $("mic-btn").classList.toggle("on", state.mic);
    for (const o of others) await callPeer(o.id, true);
  }

  async function switchNet(net) {
    if (net === state.net) return;
    for (const id of [...state.pcs.keys()]) hangup(id);
    const res = await hub.invoke("Join", state.callsign, net);
    window._census = res.census;
    state.members = [res.me, ...res.others];
    await enter(res.me.net, res.others);
    logLine("sys", "", state.callsign + " has signed into " + res.me.net);
  }

  function nudge() {
    logLine("sys", "", "You sent a nudge.");
    document.body.animate(
      [
        { transform: "translate(0)" },
        { transform: "translate(-6px, 2px)" },
        { transform: "translate(6px, -2px)" },
        { transform: "translate(0)" }
      ],
      { duration: 420, iterations: 2 }
    );
    hub.invoke("Say", "*nudges the room*");
  }

  function wireUi() {
    $("send").onclick = send;
    $("nudge").onclick = nudge;
    $("msg").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        send();
      }
    });
    $("psm")?.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        const t = $("psm").value.trim();
        if (t) hub.invoke("Say", "/me " + t);
      }
    });
    $("mic-btn").onclick = async () => {
      state.mic = !state.mic;
      if (state.localStream) for (const t of state.localStream.getAudioTracks()) t.enabled = state.mic;
      $("mic-btn").classList.toggle("on", state.mic);
      $("mic-btn").classList.toggle("off", !state.mic);
      await hub.invoke("Media", state.mic, state.cam);
    };
    $("cam-btn").onclick = async () => {
      state.cam = !state.cam;
      if (!state.localStream || state.localStream.getVideoTracks().length === 0) {
        try {
          const v = await navigator.mediaDevices.getUserMedia({
            video: { width: { ideal: 320 }, height: { ideal: 240 } }
          });
          const vt = v.getVideoTracks()[0];
          if (state.localStream) state.localStream.addTrack(vt);
          else state.localStream = v;
          for (const pc of state.pcs.values()) {
            const sender = pc.getSenders().find((s) => s.track?.kind === "video");
            if (sender) sender.replaceTrack(vt);
            else pc.addTrack(vt, state.localStream);
          }
          const self = ensureCam("local", state.callsign + " (you)", true);
          attachVideo(self.querySelector("video"), state.localStream);
        } catch (err) {
          logLine("sys", "", "Could not start webcam: " + (err.message || err));
          state.cam = false;
        }
      }
      if (state.localStream) for (const t of state.localStream.getVideoTracks()) t.enabled = state.cam;
      $("cam-btn").classList.toggle("on", state.cam);
      $("cam-btn").classList.toggle("off", !state.cam);
      showCam("local", state.cam);
      await hub.invoke("Media", state.mic, state.cam);
    };
  }

  async function send() {
    const raw = $("msg").value.trim();
    if (!raw) return;
    $("msg").value = "";
    const w = raw.match(/^\/w\s+(\S+)\s+(.+)/i);
    if (w) {
      const target = state.members.find((m) => m.callsign.toLowerCase() === w[1].toLowerCase());
      if (!target) {
        logLine("sys", "", "That contact isn't on this room.");
        return;
      }
      await hub.invoke("Whisper", target.id, w[2]);
      return;
    }
    await hub.invoke("Say", raw);
  }

  async function boot() {
    hub = new signalR.HubConnectionBuilder().withUrl("/hub").withAutomaticReconnect().build();
    hub.on("Chat", (line) => {
      if (line.kind === "whisper") logLine("whisper", line.from, "(whisper) " + line.text);
      else logLine("say", line.from, line.text);
    });
    hub.on("Joined", (m) => {
      if (!state.members.some((x) => x.id === m.id)) state.members.push(m);
      renderRoster();
      logLine("sys", "", m.callsign + " just signed in.");
      callPeer(m.id, false);
    });
    hub.on("Left", (id, callsign) => {
      state.members = state.members.filter((m) => m.id !== id);
      renderRoster();
      hangup(id);
      logLine("sys", "", (callsign || "Someone") + " signed out.");
    });
    hub.on("Media", (id, mic, cam) => {
      const m = state.members.find((x) => x.id === id);
      if (!m) return;
      m.mic = mic;
      m.cam = cam;
      showCam(id, cam);
      renderRoster();
    });
    hub.on("Talking", (id, on) => {
      const m = state.members.find((x) => x.id === id);
      if (m) m.talking = on;
      document.getElementById("cam-" + id)?.classList.toggle("talk", on);
      renderRoster();
    });
    hub.on("Signal", (fromId, kind, payload) => onSignal(fromId, kind, payload));
    await hub.start();

    const hello = await hub.invoke("Hello", "");
    state.id = hello.id;
    const sel = $("net-pick");
    sel.innerHTML = hello.nets.map((n) => `<option>${n}</option>`).join("");
    sel.value = "Area 850";
    window._census = hello.census;
    $("callsign").value = localStorage.getItem("850-callsign") || "";
    $("callsign").focus();

    $("join-btn").onclick = async () => {
      try {
        state.callsign = ($("callsign").value || "").trim() || hello.suggested;
        state.cam = $("want-cam").checked;
        state.mic = $("want-mic").checked;
        localStorage.setItem("850-callsign", state.callsign);
        const net = $("net-pick").value || "Lobby";
        $("join-btn").disabled = true;
        const res = await hub.invoke("Join", state.callsign, net);
        window._census = res.census;
        state.id = res.me.id;
        state.callsign = res.me.callsign;
        state.members = [res.me, ...res.others];
        $("splash").classList.add("hidden");
        $("app").classList.remove("hidden");
        await enter(res.me.net, res.others);
        await hub.invoke("Media", state.mic, state.cam);
        logLine("sys", "", "You have signed in. Webcam " + (state.cam ? "on" : "off") + ", mic " + (state.mic ? "open (duplex)" : "muted") + ".");
      } catch (err) {
        alert("Could not sign in: " + (err.message || err));
        $("join-btn").disabled = false;
      }
    };
    wireUi();
  }

  boot().catch((e) => alert("850 Messenger failed to start: " + e));
})();
