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

  // "This browser has paused playback" (TVAPP-00202) needs a click on Resume Playback, and
  // "Are you still watching?" needs Continue. Runs on every tuner evaluate, which carries a user gesture.
  const dismissPlayerDialogs = () => {
    try {
      const errorPanel = document.querySelector('tv-player-panel-error#errorPanel');
      if (errorPanel && !errorPanel.hidden) {
        const page = errorPanel.querySelector('tv-page-error-content.iron-selected');
        const title = page && page.querySelector('#title');
        if (title && /paused playback/i.test(title.textContent)) {
          const resume = page.querySelector('tv-button button');
          if (resume) resume.click();
        }
      }

      const stillWatching = document.getElementById('playerStillWatching');
      if (stillWatching && getComputedStyle(stillWatching).display !== 'none') {
        const cont = stillWatching.querySelector('#continueButton button');
        if (cont) cont.click();
        else if (typeof stillWatching._onContinue === 'function') stillWatching._onContinue();
      }
    } catch {}
  };
  dismissPlayerDialogs();

  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;

  const injectStaticCss = () => {
    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
      const s = document.createElement('style');
      s.id = 'charon-static-css';
      s.textContent = `
        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
        tv-player-nav, tv-player-rating, tv-player-mini-controls, tv-player-buffer-indicator,
        tv-player-notification, tv-player-network-alert, tv-player-debug-panel, tv-player-panel-loading,
        tv-player-panel-next-episode, tv-player-panel-caster, tv-overlay-player-info, tv-overlay-player-ooh-info,
        #playerControlPanel, #nextEpisodeControls, #playerLastWatchedPanel, #inner-cc-container {
          display: none !important;
        }
        tv-player-still-watching { opacity: 0 !important; }
      `;
      (document.head || document.documentElement).appendChild(s);
    }
  };
  injectStaticCss();

  // The Viper player's <video>; #viperPlayerFreewheelVideo is the ad player
  const findMainVideo = () => document.querySelector('#viper-player-container video') ||
    Array.from(document.querySelectorAll('video'))
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

  // Called by ChromeTuner before it sends a trusted click to unpause the player.
  // A center click only reveals the (hidden) control bar; it does not toggle pause.
  window.__charonIsPaused = () => {
    dismissPlayerDialogs();
    const v = findMainVideo();
    if (!v) return false;
    return v.paused;
  };

  const checkPlayer = () => {
    injectStaticCss();
    dismissPlayerDialogs();

    const allVideos = Array.from(document.querySelectorAll('video'));
    if (allVideos.length === 0) return;

    for (const v of allVideos) {
      if (v.muted) v.muted = false;
      if (v.volume < 1.0) v.volume = 1.0;
      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
        v.play().catch(() => {});
      }
    }

    const readyVideos = allVideos
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
    }
  };

  setInterval(checkPlayer, 300);
})();