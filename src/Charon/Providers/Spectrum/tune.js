(function() {
  const v = document.querySelector('video');
  if (v) {
    v.muted = false;
    v.volume = 1.0;
    if (v.paused) {
      v.play().catch(() => {});
    }
  }
})();
