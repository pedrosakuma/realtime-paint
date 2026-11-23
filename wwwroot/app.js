const width = 256;
const height = 256;
const colors = ["#ffffff", "#111827", "#ef4444", "#22c55e", "#3b82f6", "#f59e0b"];
const canvas = document.querySelector("#board");
const context = canvas.getContext("2d", { alpha: false });
const status = document.querySelector("#status");
let selectedColor = 1;
let drawing = false;
let lastPoint = null;

context.fillStyle = colors[0];
context.fillRect(0, 0, width, height);

const protocol = location.protocol === "https:" ? "wss:" : "ws:";
const socket = new WebSocket(`${protocol}//${location.host}/ws`);

socket.addEventListener("open", () => {
  status.textContent = "Conectado";
  status.classList.add("connected");
});

socket.addEventListener("close", () => {
  status.textContent = "Conexão encerrada. Recarregue para reconectar.";
  status.classList.remove("connected");
});

socket.addEventListener("message", event => {
  const message = JSON.parse(event.data);
  if (message.type === "snapshot") {
    renderSnapshot(message.cells);
  } else if (message.type === "stroke") {
    renderStroke(message);
  }
});

document.querySelectorAll(".swatch").forEach(button => {
  button.addEventListener("click", () => {
    selectedColor = Number(button.dataset.color);
    const selected = document.querySelector(".swatch.selected");
    selected.classList.remove("selected");
    selected.setAttribute("aria-pressed", "false");
    button.classList.add("selected");
    button.setAttribute("aria-pressed", "true");
  });
});

canvas.addEventListener("pointerdown", event => {
  if (socket.readyState !== WebSocket.OPEN) return;
  drawing = true;
  lastPoint = toPoint(event);
  canvas.setPointerCapture(event.pointerId);
  sendStroke([lastPoint]);
});

canvas.addEventListener("pointermove", event => {
  if (!drawing) return;
  sendPointerSegment(event);
});

canvas.addEventListener("pointerup", event => finishStroke(event, true));
canvas.addEventListener("pointercancel", () => finishStroke(null, false));

function toPoint(event) {
  const bounds = canvas.getBoundingClientRect();
  return {
    x: Math.max(0, Math.min(width - 1, Math.floor((event.clientX - bounds.left) * width / bounds.width))),
    y: Math.max(0, Math.min(height - 1, Math.floor((event.clientY - bounds.top) * height / bounds.height)))
  };
}

function finishStroke(event, includeLastPoint) {
  if (!drawing) return;
  if (includeLastPoint && event) sendPointerSegment(event);
  drawing = false;
  lastPoint = null;
}

function sendPointerSegment(event) {
  const point = toPoint(event);
  if (point.x !== lastPoint.x || point.y !== lastPoint.y) {
    sendStroke([lastPoint, point]);
    lastPoint = point;
  }
}

function sendStroke(points) {
  if (socket.readyState === WebSocket.OPEN) {
    const request = { type: "stroke", color: selectedColor, points };
    socket.send(JSON.stringify(request));
  }
}

function renderSnapshot(cells) {
  const image = context.createImageData(width, height);
  for (let index = 0; index < cells.length; index++) {
    const hex = colors[cells[index]].slice(1);
    const offset = index * 4;
    image.data[offset] = parseInt(hex.slice(0, 2), 16);
    image.data[offset + 1] = parseInt(hex.slice(2, 4), 16);
    image.data[offset + 2] = parseInt(hex.slice(4, 6), 16);
    image.data[offset + 3] = 255;
  }
  context.putImageData(image, 0, 0);
}

function renderStroke(stroke) {
  const [first, ...rest] = stroke.points;
  context.beginPath();
  context.moveTo(first.x + 0.5, first.y + 0.5);
  for (const point of rest) context.lineTo(point.x + 0.5, point.y + 0.5);
  if (rest.length === 0) context.lineTo(first.x + 0.5, first.y + 0.5);
  context.strokeStyle = colors[stroke.color];
  context.lineWidth = 1;
  context.lineCap = "round";
  context.lineJoin = "round";
  context.stroke();
}
