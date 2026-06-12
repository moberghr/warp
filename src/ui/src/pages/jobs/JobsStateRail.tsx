import { GroupStateRail } from './GroupStateRail';

interface JobsStateRailProps {
  active: string;
}

export function JobsStateRail({ active }: JobsStateRailProps) {
  return <GroupStateRail kind="jobs" active={active} />;
}
