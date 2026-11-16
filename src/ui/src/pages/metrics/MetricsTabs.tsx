import { NavLink } from 'react-router-dom';

const tabClass = ({ isActive }: { isActive: boolean }) =>
  `px-3 py-1.5 text-sm border-b-2 -mb-px ${isActive ? 'border-primary text-primary font-medium' : 'border-transparent text-muted-foreground hover:text-foreground'}`;

/** The Metrics page's two views: the instrument list and the health board (#326). */
export function MetricsTabs() {
  return (
    <nav className="mb-4 flex gap-1 border-b border-border" aria-label="Metrics views">
      <NavLink to="/metrics" end className={tabClass}>Instruments</NavLink>
      <NavLink to="/metrics/board" className={tabClass}>Board</NavLink>
    </nav>
  );
}
