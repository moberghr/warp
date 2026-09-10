import { useLocation } from 'react-router-dom';
import { NAV_ENTRIES, groupsOf, resolveActiveLocation } from '@/layouts/navModel';

/**
 * The page title, prefixed with its nav group when it has one. Grouping the top
 * nav hides which section a page belongs to behind a dropdown, so the heading is
 * where that context comes back. The group is derived from the route against the
 * same table the header uses — including the host's own layout, so the breadcrumb
 * names the group the host declared rather than the built-in one. Ungated, since a
 * page you're standing on belongs to its group whether or not the addon probe has
 * answered yet.
 */
export function PageHeading({
  children,
  className = 'mb-6',
}: {
  children: React.ReactNode;
  className?: string;
}) {
  const location = useLocation();
  const { group } = resolveActiveLocation(location.pathname, [], groupsOf(NAV_ENTRIES));

  return (
    <h1 className={`flex items-baseline gap-2 text-2xl font-bold ${className}`}>
      {group && (
        <>
          <span className="font-medium text-muted-foreground">{group.label}</span>
          <span className="font-normal text-muted-foreground">/</span>
        </>
      )}
      <span>{children}</span>
    </h1>
  );
}
