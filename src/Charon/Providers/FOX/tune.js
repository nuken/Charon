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

  // Capture #charon_channel=CODE before FOX's SPA router strips the hash
  try {
    const hashMatch = location.hash.match(/charon_channel=([^&]+)/i);
    if (hashMatch) {
      sessionStorage.setItem('__charonFoxTarget', decodeURIComponent(hashMatch[1]));
    }
  } catch {}

  const navigateFromGuide = () => {
    if (!location.pathname.startsWith('/live')) return false;

    let targetCode = '';
    try { targetCode = sessionStorage.getItem('__charonFoxTarget') || ''; } catch {}
    if (!targetCode) return false;

    const now = Date.now();

    // Step 1: Click the target channel logo in the guide
    if (window.__charonFoxLogoClicked !== targetCode) {
      const logoBtn = document.querySelector(`[data-testid="GuideChannelLogo"] button[aria-label="${targetCode}"]`)
        || Array.from(document.querySelectorAll('[data-testid="GuideChannelLogo"] button'))
             .find(b => (b.getAttribute('aria-label') || b.querySelector('img')?.getAttribute('alt') || '').toUpperCase() === targetCode.toUpperCase());

      if (logoBtn) {
        window.__charonFoxLogoClicked = targetCode;
        window.__charonFoxLogoClickedAt = now;
        logoBtn.click();
      }
      return true;
    }

    // Step 2: If clicking the logo only selected the channel in the hero banner, click "Watch Live"
    if (now - (window.__charonFoxLogoClickedAt || 0) >= 500 && now - (window.__charonFoxWatchClickedAt || 0) > 2500) {
      const watchLink = document.querySelector('a[href^="/watch/epg/"]');
      if (watchLink) {
        window.__charonFoxWatchClickedAt = now;
        watchLink.click();
      }
    }
    return true;
  };

  const findMainVideo = () =>
    Array.from(document.querySelectorAll('video'))
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

  window.__charonIsPaused = () => {
    if (location.pathname.startsWith('/live')) return false;
    const v = findMainVideo();
    if (!v) return false;
    return v.paused;
  };

  const checkPlayer = () => {
    if (navigateFromGuide()) return;

    const v = findMainVideo();
    if (!v) return;

    if (v.paused) {
      const playBtn = document.querySelector('button[aria-label*="Play" i], button[title*="Play" i]');
      if (playBtn) playBtn.click();
      v.play().catch(() => {});
    }

    if (!v.paused && (v.muted || v.volume === 0)) {
      const unmuteBtn = document.querySelector('button[aria-label*="Unmute" i], button[title*="Unmute" i], button[aria-label*="Mute" i]');
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