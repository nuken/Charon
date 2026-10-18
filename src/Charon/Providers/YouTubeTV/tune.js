(function () {
  try {
    const blockHeavyCodecs = (mimeType) => {
      if (typeof mimeType !== 'string') return false;
      const lower = mimeType.toLowerCase();
      return lower.includes('av01');
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

    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
      window.__charonQualityLocked = true;
      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
      if (origGetQuality) {
        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
          const q = origGetQuality.call(this);
          return {
            creationTime: q.creationTime,
            totalVideoFrames: q.totalVideoFrames,
            droppedVideoFrames: 0,
            corruptedVideoFrames: 0
          };
        };
      }
    }
  } catch {}

  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;

  const injectStaticCss = () => {
    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
      const s = document.createElement('style');
      s.id = 'charon-static-css';
      s.textContent = `
        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
        ytlr-header, ytu-header, ytu-nav-bar, ytu-guide, ytu-epg, ytu-epg-row,
        .ytp-chrome-top, .ytp-chrome-bottom, .ytp-gradient-top, .ytp-gradient-bottom,
        .ytp-pause-overlay, .ytp-spinner, tp-yt-paper-dialog, ytu-upsell-dialog {
          display: none !important;
        }
      `;
      (document.head || document.documentElement).appendChild(s);
    }
  };
  injectStaticCss();

  const checkPlayer = () => {
    injectStaticCss();

    try {
      const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
      if (player) {
        if (typeof player.unMute === 'function') player.unMute();
        if (typeof player.setVolume === 'function') player.setVolume(100);
        if (typeof player.playVideo === 'function' && typeof player.getPlayerState === 'function') {
          const st = player.getPlayerState();
          if (st === -1 || st === 2 || st === 5) player.playVideo();
        }
      }
    } catch {}

    const allVideos = Array.from(document.querySelectorAll('video'));
    if (allVideos.length === 0) return;

    const playerVideos = allVideos.filter((v) => !v.closest('ytu-epg-row, ytu-guide, .tenx-thumb'));
    for (const v of playerVideos) {
      if (v.muted) v.muted = false;
      if (v.volume < 1.0) v.volume = 1.0;
      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
        v.play().catch(() => {});
      }
    }

    const readyVideos = playerVideos
      .filter((v) => v.readyState >= 2 && v.videoWidth > 0 && !v.paused)
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight));

    const activeVideo = readyVideos[0];
    if (!activeVideo) return;

    for (const v of allVideos) {
      if (v !== activeVideo && !v.paused) {
        v.muted = true;
        try { v.pause(); } catch {}
      }
    }

    if (!activeVideo.__charonStyled) {
      activeVideo.__charonStyled = true;
      activeVideo.style.cssText = [
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
        'z-index: 999999 !important'
      ].join('; ');

      try {
        const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
        if (player && typeof player.setPlaybackQualityRange === 'function') {
          player.setPlaybackQualityRange('hd720', 'hd1080');
        }
      } catch {}
    }
  };

  setInterval(checkPlayer, 300);
})();