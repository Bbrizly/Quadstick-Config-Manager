// Reusable CSS-animation marquee. Interaction only changes playback rate.
(function () {
  function updatePlaybackRate(track, rate) {
    const animation = track.getAnimations()[0];
    animation?.updatePlaybackRate(rate);
  }

  function getInteractivePlaybackRate(x, width, interactiveSpeed) {
    if (width <= 0) return 1;
    const edgeZone = width * 0.2;
    if (x < edgeZone) return -Math.abs(interactiveSpeed);
    if (x > width - edgeZone) return Math.abs(interactiveSpeed);
    return 1;
  }

  function createMarquee(container, track, options) {
    const {
      speed = 1,
      inverted = true,
      paused = false,
      pauseOnHover = false,
      interactiveSpeed,
    } = options || {};
    const absoluteSpeed = Math.abs(speed) || 1;
    const animationDuration = `${50000 / absoluteSpeed / 1000}s`;
    const animationDirection = inverted !== (speed < 0) ? 'reverse' : 'normal';

    const originalChildren = [...track.children];
    const duplicateChildren = originalChildren.map(child => {
      const duplicate = child.cloneNode(true);
      duplicate.setAttribute('aria-hidden', 'true');
      if (duplicate.matches('a')) duplicate.tabIndex = -1;
      duplicate.querySelectorAll?.('a').forEach(link => { link.tabIndex = -1; });
      return duplicate;
    });
    track.append(...duplicateChildren);

    // it also holds still offscreen (no work nobody sees) and while a mark has
    // keyboard focus, so the link being read is not sliding away.
    let isHovered = false, isOffscreen = false, isFocused = false;
    const updatePlayState = () => {
      track.style.animationPlayState = paused || isOffscreen || isFocused || (pauseOnHover && isHovered)
        ? 'paused'
        : 'running';
    };
    track.style.animationDuration = animationDuration;
    track.style.animationDirection = animationDirection;
    updatePlayState();

    if ('IntersectionObserver' in window) {
      new IntersectionObserver(([entry]) => {
        isOffscreen = !entry.isIntersecting;
        updatePlayState();
      }).observe(container);
    }
    container.addEventListener('focusin', () => { isFocused = true; updatePlayState(); });
    container.addEventListener('focusout', () => { isFocused = false; updatePlayState(); });

    if (pauseOnHover) {
      container.addEventListener('mouseenter', () => {
        isHovered = true;
        updatePlayState();
      });
    }
    if (interactiveSpeed) {
      container.addEventListener('mousemove', event => {
        const rect = event.currentTarget.getBoundingClientRect();
        const x = event.clientX - rect.left;
        updatePlaybackRate(track, getInteractivePlaybackRate(x, rect.width, interactiveSpeed));
      });
    }
    container.addEventListener('mouseleave', () => {
      isHovered = false;
      updatePlayState();
      if (interactiveSpeed) updatePlaybackRate(track, 1);
    });
  }

  window.createMarquee = createMarquee;
})();
