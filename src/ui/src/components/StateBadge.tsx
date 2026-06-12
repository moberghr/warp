import { Badge } from '@/components/ui/badge';
import { State, CancellationMode } from '@/types';
import { stateName, stateColor } from '@/utils/format';

export function StateBadge({ state, cancellationMode }: { state: State; cancellationMode?: CancellationMode }) {
  if (state === State.Processing && cancellationMode != null && cancellationMode !== CancellationMode.None) {
    return (
      <Badge variant="outline" className="bg-state-cancelling-bg text-state-cancelling border-transparent">
        Cancelling...
      </Badge>
    );
  }

  return (
    <Badge variant="outline" className={stateColor(state)}>
      {stateName(state)}
    </Badge>
  );
}
