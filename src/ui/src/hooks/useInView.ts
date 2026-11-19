import { useEffect, useState } from 'react';

/**
 * Whether the element (attach the returned ref callback) is on screen, or within `rootMargin` of it. A board's chart
 * tiles use it to start their series request only once scrolled into view and to stop refetching while scrolled away,
 * so a board opened at the top does not fetch twelve charts. Without IntersectionObserver every element reads visible.
 */
export function useInView<T extends Element>(rootMargin = '200px'): [(node: T | null) => void, boolean] {
  const [node, setNode] = useState<T | null>(null);
  const [inView, setInView] = useState(() => typeof IntersectionObserver === 'undefined');

  useEffect(() => {
    if (node === null || typeof IntersectionObserver === 'undefined') {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          setInView(entry.isIntersecting);
        }
      },
      { rootMargin },
    );
    observer.observe(node);

    return () => observer.disconnect();
  }, [node, rootMargin]);

  return [setNode, inView];
}
