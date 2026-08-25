import { useNavigate } from 'react-router-dom';
import { Card, CardContent } from '@/components/ui/card';

interface MetricCardProps {
  label: string;
  value: number;
  icon?: React.ReactNode;
  color?: string;
  href?: string;
  /** Unit appended to the value, e.g. "%" for a rate. Without it a percentage reads as a bare count. */
  suffix?: string;
}

export function MetricCard({ label, value, icon, color, href, suffix }: MetricCardProps) {
  const navigate = useNavigate();

  return (
    <Card
      className={href ? 'cursor-pointer hover:bg-accent/50 transition-colors' : ''}
      onClick={href ? () => navigate(href) : undefined}
    >
      <CardContent className="p-4">
        <div className="flex items-center justify-between">
          <div>
            <p className="text-sm text-muted-foreground">{label}</p>
            <p className={`text-2xl font-bold ${color ?? ''}`}>{value.toLocaleString()}{suffix}</p>
          </div>
          {icon && <div className="text-muted-foreground">{icon}</div>}
        </div>
      </CardContent>
    </Card>
  );
}
