import React, {useCallback, useEffect, useState} from 'react';
import useBaseUrl from '@docusaurus/useBaseUrl';

interface ScreenshotProps {
  light: string;
  dark: string;
  alt: string;
}

/**
 * A dashboard screenshot, in the reader's theme, that opens full size on click.
 *
 * The shots are captured at 1920px and rendered into a ~800px content column, so at rest most of
 * them are illustrative rather than readable — the zoom is what makes a table of counter values
 * worth putting in the docs at all.
 *
 * Both the light and dark images are always rendered and CSS picks one (see custom.css
 * `img[data-theme-target]`), rather than branching on `useColorMode`, so the correct one is in the
 * server-rendered HTML and there is no flash on hydration. The overlay follows the same rule.
 */
export default function Screenshot({light, dark, alt}: ScreenshotProps) {
  const lightSrc = useBaseUrl(light);
  const darkSrc = useBaseUrl(dark);
  const [zoomed, setZoomed] = useState(false);

  const close = useCallback(() => setZoomed(false), []);

  useEffect(() => {
    if (!zoomed) {
      return undefined;
    }

    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        close();
      }
    };

    // The page behind a full-screen overlay should not scroll away under it.
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    document.addEventListener('keydown', onKeyDown);

    return () => {
      document.body.style.overflow = previousOverflow;
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [zoomed, close]);

  const thumbnail = (src: string, target: 'light' | 'dark') => (
    <img
      key={target}
      src={src}
      alt={alt}
      data-theme-target={target}
      style={{width: '100%', cursor: 'zoom-in'}}
    />
  );

  return (
    <>
      <button
        type="button"
        onClick={() => setZoomed(true)}
        aria-label={`${alt} — click to enlarge`}
        style={{
          display: 'block',
          width: '100%',
          padding: 0,
          border: 0,
          background: 'none',
          cursor: 'zoom-in',
        }}
      >
        {thumbnail(lightSrc, 'light')}
        {thumbnail(darkSrc, 'dark')}
      </button>

      {zoomed && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={alt}
          onClick={close}
          style={{
            position: 'fixed',
            inset: 0,
            zIndex: 400,
            display: 'flex',
            alignItems: 'flex-start',
            justifyContent: 'center',
            padding: '2rem',
            background: 'rgba(0, 0, 0, 0.8)',
            cursor: 'zoom-out',
            overflow: 'auto',
          }}
        >
          <img
            src={lightSrc}
            alt={alt}
            data-theme-target="light"
            style={{maxWidth: '100%', height: 'auto'}}
          />
          <img
            src={darkSrc}
            alt={alt}
            data-theme-target="dark"
            style={{maxWidth: '100%', height: 'auto'}}
          />
        </div>
      )}
    </>
  );
}
