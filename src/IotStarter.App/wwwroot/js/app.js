// Small platform helpers for the Blazor UI: theme, direction and pull-to-refresh.
(function () {
    const LIGHT_CSS = '_content/AntDesign/css/ant-design-blazor.variable.css';
    const DARK_CSS = '_content/AntDesign/css/ant-design-blazor.dark.css';
    const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');
    let mode = 'system';

    function setDark(dark) {
        const root = document.documentElement;
        root.dataset.theme = dark ? 'dark' : 'light';
        const link = document.getElementById('antd-theme');
        const href = dark ? DARK_CSS : LIGHT_CSS;
        if (link && link.getAttribute('href') !== href) {
            link.setAttribute('href', href);
        }

        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) {
            meta.setAttribute('content', dark ? '#0B0F14' : '#F6F7F9');
        }
    }

    darkQuery.addEventListener('change', () => {
        if (mode === 'system') {
            setDark(darkQuery.matches);
        }
    });

    window.iotStarter = {
        // mode: 'light' | 'dark' | 'system'
        applyTheme(newMode) {
            mode = newMode;
            setDark(mode === 'dark' || (mode === 'system' && darkQuery.matches));
        },

        setDirection(dir, lang) {
            document.documentElement.setAttribute('dir', dir);
            document.documentElement.setAttribute('lang', lang);
        },

        scrollToTop() {
            const el = document.querySelector('.page-scroll');
            if (el) {
                el.scrollTop = 0;
            }
        },

        // Pull-to-refresh on a scroll container; calls dotnetRef.OnPulled() when released past the threshold.
        attachPullToRefresh(el, dotnetRef) {
            if (!el || el._ptr) {
                return;
            }

            const THRESHOLD = 64;
            const MAX = 96;
            let startY = 0;
            let pulling = false;
            let distance = 0;

            const onStart = (e) => {
                if (el.scrollTop <= 0 && e.touches.length === 1) {
                    startY = e.touches[0].clientY;
                    pulling = true;
                    distance = 0;
                    el.classList.add('ptr-dragging');
                }
            };

            const onMove = (e) => {
                if (!pulling) {
                    return;
                }

                const delta = e.touches[0].clientY - startY;
                if (delta <= 0 || el.scrollTop > 0) {
                    distance = 0;
                    el.style.setProperty('--ptr-offset', '0px');
                    return;
                }

                distance = Math.min(delta * 0.5, MAX);
                el.style.setProperty('--ptr-offset', distance + 'px');
                el.classList.toggle('ptr-ready', distance >= THRESHOLD);
                if (e.cancelable) {
                    e.preventDefault();
                }
            };

            const onEnd = () => {
                if (!pulling) {
                    return;
                }

                pulling = false;
                const ready = distance >= THRESHOLD;
                el.classList.remove('ptr-dragging', 'ptr-ready');
                el.style.setProperty('--ptr-offset', '0px');
                if (ready) {
                    dotnetRef.invokeMethodAsync('OnPulled');
                }
            };

            el.addEventListener('touchstart', onStart, { passive: true });
            el.addEventListener('touchmove', onMove, { passive: false });
            el.addEventListener('touchend', onEnd);
            el.addEventListener('touchcancel', onEnd);
            el._ptr = { onStart, onMove, onEnd };
        },

        detachPullToRefresh(el) {
            if (!el || !el._ptr) {
                return;
            }

            el.removeEventListener('touchstart', el._ptr.onStart);
            el.removeEventListener('touchmove', el._ptr.onMove);
            el.removeEventListener('touchend', el._ptr.onEnd);
            el.removeEventListener('touchcancel', el._ptr.onEnd);
            delete el._ptr;
        }
    };

    // Apply the system theme immediately so the first paint matches; Blazor re-applies the saved choice.
    setDark(darkQuery.matches);
})();
