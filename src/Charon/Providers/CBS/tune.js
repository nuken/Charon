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

  // Automatically dismiss the CBS MVPD "Start Watching" confirmation splash screen during tuning
  const dismissSplash = () => {
    try {
      const buttons = Array.from(document.querySelectorAll('button, a, div[role="button"], span, .button'));
      const startBtn = buttons.find(el => {
        const t = (el.textContent || '').trim().toLowerCase();
        return t === 'start watching' || t === 'start watching »' || el.getAttribute('aria-label')?.toLowerCase() === 'start watching';
      });
      if (startBtn && Date.now() - (window.__charonCbsClickAt || 0) > 2000) {
        window.__charonCbsClickAt = Date.now();
        startBtn.click();
      }
    } catch {}
  };
  dismissSplash();

  const handleErrors = () => {
    try {
      const bodyText = document.body.innerText || '';
      if (bodyText.includes('This local station is not supported')) {
        const homeBtn = Array.from(document.querySelectorAll('a, button'))
          .find(el => /go to home/i.test(el.textContent || ''));
        if (homeBtn && Date.now() - (window.__charonCbsRetryAt || 0) > 5000) {
          window.__charonCbsRetryAt = Date.now();
          homeBtn.click();
        } else if (!window.__charonCbsReloaded) {
          window.__charonCbsReloaded = true;
          setTimeout(() => location.reload(), 3000);
        }
      }
    } catch {}
  };
  handleErrors();

  const queryAllDeep = (selector, root = document) => {
    const results = Array.from(root.querySelectorAll(selector));
    const all = root.querySelectorAll('*');
    for (const el of all) {
      if (el.shadowRoot) {
        results.push(...queryAllDeep(selector, el.shadowRoot));
      }
    }
    return results;
  };

  const findMainVideo = () =>
    queryAllDeep('video')
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

  window.__charonIsPaused = () => {
    dismissSplash();
    handleErrors();
    const v = findMainVideo();
    if (!v) return false;
    return v.paused;
  };

  const checkPlayer = () => {
    dismissSplash();
    handleErrors();
    const v = findMainVideo();
    if (!v) return;

    if (v.paused) {
      const playBtn = queryAllDeep('button[aria-label*="Play" i], avia-button[aria-label*="Play" i]')[0];
      if (playBtn) playBtn.click();
      v.play().catch(() => {});
    }

    if (!v.paused && (v.muted || v.volume === 0)) {
      const unmuteBtn = queryAllDeep('button[aria-label*="Mute" i], avia-button[aria-label*="Mute" i]')[0];
      if (unmuteBtn) unmuteBtn.click();
      v.muted = false;
      v.volume = 1.0;
    } else if (!v.paused && v.volume < 1.0) {
      v.volume = 1.0;
    }

    if (!v.__charonStyled) {
      v.__charonStyled = true;
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
        'position: fixed !important',
        'z-index: 2147483647 !important'
      ].join('; ');
    }
  };

  checkPlayer();
  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;
  setInterval(checkPlayer, 300);
})();