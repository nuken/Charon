(function () {
  try {
    const blockHeavyCodecs = (mimeType) => {
      if (typeof mimeType !== 'string') return false;
      return mimeType.toLowerCase().includes('av01');
    };
    if (window.MediaSource && !window.__charonCodecLocked) {
      window.__charonCodecLocked = true;
      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
      window.MediaSource.isTypeSupported = function (mimeType) {
        if (blockHeavyCodecs(mimeType)) return false;
        return origIsTypeSupported(mimeType);
      };
    }
    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
      window.__charonCanPlayLocked = true;
      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
        if (blockHeavyCodecs(mimeType)) return '';
        return origCanPlayType.call(this, mimeType);
      };
    }
    if (navigator.userActivation) {
      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
    }
  } catch {}

  const queryAllDeep = (selector, root = document) => {
    const results = Array.from(root.querySelectorAll(selector));
    const all = root.querySelectorAll('*');
    for (const el of all) {
      if (el.shadowRoot) results.push(...queryAllDeep(selector, el.shadowRoot));
    }
    return results;
  };

  // On /watch/catalog/<uuid>, jump straight into the first Live Now tile (#bucketId=1)
  const navigateFromCatalog = () => {
    if (!location.pathname.startsWith('/watch/catalog/')) return false;

    if (!window.__charonEspnNavigated) {
      const liveLink = document.querySelector('a[href*="/watch/player/"][href*="#bucketId=1"]')
        || document.querySelector('a[href*="/watch/player/"]');
      if (liveLink) {
        window.__charonEspnNavigated = true;
        liveLink.click();
      }
    }
    return true;
  };

  const findMainVideo = () =>
    queryAllDeep('video')
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

  window.__charonIsPaused = () => {
    if (location.pathname.startsWith('/watch/catalog/')) return false;
    const v = findMainVideo();
    if (!v) return false;
    return v.paused;
  };

  const checkPlayer = () => {
    if (navigateFromCatalog()) return;

    // Hide any Shadow DOM UI overlays inside fitt-web-player-ui / disney-web-player
    for (const ui of queryAllDeep('fitt-web-player-ui, .DxcOverlay, [class*="controls" i], [class*="overlay" i]')) {
      if (ui.tagName !== 'VIDEO' && !ui.querySelector?.('video') && ui.style) {
        ui.style.setProperty('opacity', '0', 'important');
        ui.style.setProperty('pointer-events', 'none', 'important');
      }
    }

    const v = findMainVideo();
    if (!v) return;

    if (v.paused) {
      const playBtn = queryAllDeep('button[aria-label*="Play" i], button[title*="Play" i]')[0];
      if (playBtn) playBtn.click();
      v.play().catch(() => {});
    }

    if (!v.paused && (v.muted || v.volume === 0)) {
      const unmuteBtn = queryAllDeep('button[aria-label*="Unmute" i], button[title*="Unmute" i], button[aria-label*="Mute" i][aria-pressed="true"]')[0];
      if (unmuteBtn) unmuteBtn.click();
      v.muted = false;
      v.volume = 1.0;
    } else if (!v.paused && v.volume < 1.0) {
      v.volume = 1.0;
    }

    if (!v.__charonStyled && !v.paused && v.readyState >= 2 && v.videoWidth > 0 && v.currentTime > 0.2) {
      v.__charonStyled = true;
      document.documentElement.classList.add('charon-playing');
      v.style.cssText = [
        'background: black !important',
        'cursor: none !important',
        'display: block !important',
        'height: 100vh !important',
        'width: 100vw !important',
        'left: 0 !important',
        'top: 0 !important',
        'margin: 0 !important',
        'max-height: none !important',
        'max-width: none !important',
        'object-fit: fill !important',
        'opacity: 1 !important',
        'position: fixed !important',
        'z-index: 2147483646 !important'
      ].join('; ');
    }
  };

  checkPlayer();
  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;
  setInterval(checkPlayer, 300);
})();