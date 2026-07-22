import { useState, useEffect, useMemo } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import {
  ReactFlow,
  Controls,
  Background,
  BackgroundVariant,
  type Node,
  type Edge,
  type NodeTypes,
  type NodeProps,
  Handle,
  Position,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import dagre from '@dagrejs/dagre';
import { StateBadge } from '@/components/StateBadge';
import { cappedChildSlots } from '@/lib/traceLayout';
import { shortType, shortId } from '@/utils/format';
import { LoadingState, ErrorState } from '@/components/PageState';
import { Briefcase, Mail, Layers } from 'lucide-react';
import type { State } from '@/types';
import type { TraceJobModel } from '@/types';
import * as api from '@/api';

const NODE_WIDTH = 220;
const NODE_HEIGHT = 72;
const GROUP_PADDING = 30;
const CHILD_GAP = 16;

function kindIcon(kind: number) {
  if (kind === 3) return <Layers className="h-4 w-4 text-muted-foreground" />;
  if (kind === 2) return <Mail className="h-4 w-4 text-muted-foreground" />;
  return <Briefcase className="h-4 w-4 text-muted-foreground" />;
}

function kindLabel(kind: number) {
  if (kind === 3) return 'Batch';
  if (kind === 2) return 'Message';
  return 'Job';
}


// Is this a parallel child (batch child or message handler)?
function isParallelChild(child: TraceJobModel, parent: TraceJobModel | undefined): boolean {
  if (!parent) return false;
  // Batch → Job or Message → Job = parallel children
  return (parent.kind === 3 || parent.kind === 2) && child.kind === 1;
}

function TraceNode({ data }: NodeProps) {
  const navigate = useNavigate();
  const job = data as unknown as TraceJobModel & { highlighted?: boolean };
  const highlighted = job.highlighted;
  return (
    <div
      className={`border rounded-lg px-3 py-2 shadow-sm cursor-pointer hover:border-primary transition-colors ${highlighted ? 'ring-2 ring-primary border-primary bg-primary/10' : 'bg-card'}`}
      style={{ width: NODE_WIDTH, minHeight: NODE_HEIGHT }}
      onClick={() => navigate(`/detail/${job.id}`)}
    >
      <Handle type="target" position={Position.Left} className="!bg-transparent !border-0 !w-0 !h-0" />
      <div className="flex items-center gap-2 mb-1">
        {kindIcon(job.kind)}
        <span className="text-xs text-muted-foreground">{kindLabel(job.kind)}</span>
        <span className="text-xs font-mono text-muted-foreground ml-auto">{shortId(job.id)}</span>
      </div>
      <div className="text-sm font-medium truncate">{shortType(job.type)}</div>
      <div className="flex items-center gap-2 mt-1">
        <StateBadge state={job.currentState} />
        {job.handlerType && <span className="text-xs text-muted-foreground truncate">{shortType(job.handlerType)}</span>}
      </div>
      <Handle type="source" position={Position.Right} className="!bg-transparent !border-0 !w-0 !h-0" />
    </div>
  );
}

function GroupNode({ data }: NodeProps) {
  const navigate = useNavigate();
  const d = data as unknown as { label: string; id: string; kind: number; state: State; highlighted?: boolean };
  return (
    <div
      className={`border-2 border-dashed border-muted-foreground/30 rounded-xl ${d.highlighted ? 'border-primary bg-primary/5' : ''}`}
      style={{ width: '100%', height: '100%', position: 'relative' }}
    >
      <Handle type="target" position={Position.Left} className="!bg-transparent !border-0 !w-0 !h-0" />
      <span
        className="absolute -top-3 left-3 bg-card px-2 text-xs font-medium cursor-pointer text-primary hover:underline whitespace-nowrap flex items-center gap-1"
        onClick={() => navigate(`/detail/${d.id}`)}
      >
        {kindIcon(d.kind)} {d.label} <StateBadge state={d.state} />
      </span>
      <Handle type="source" position={Position.Right} className="!bg-transparent !border-0 !w-0 !h-0" />
    </div>
  );
}

const nodeTypes: NodeTypes = { traceNode: TraceNode, group: GroupNode };

function buildGraph(jobs: TraceJobModel[], highlightId?: string): { nodes: Node[]; edges: Edge[] } {
  const jobMap = new Map(jobs.map(j => [j.id, j]));
  const nodes: Node[] = [];
  const edges: Edge[] = [];

  // Identify containers (batches/messages that have parallel children in this trace)
  const containers = new Map<string, TraceJobModel[]>(); // parentId → children
  for (const job of jobs) {
    if (job.parentJobId && jobMap.has(job.parentJobId)) {
      const parent = jobMap.get(job.parentJobId)!;
      if (isParallelChild(job, parent)) {
        if (!containers.has(job.parentJobId)) containers.set(job.parentJobId, []);
        containers.get(job.parentJobId)!.push(job);
      }
    }
  }

  // Track containers and their children
  const childrenInContainer = new Set<string>();
  for (const children of containers.values()) {
    for (const c of children) childrenInContainer.add(c.id);
  }

  // #241: a container that fans out to hundreds/thousands of children used to render every child as its
  // own node, making the graph unusable. Cap the individually-rendered children per container and collapse
  // the remainder into a single "+N more" summary node (container children carry no individual edges, so
  // hiding a node needs no edge cleanup).
  const CHILD_RENDER_CAP = 24;
  const hiddenChildIds = new Set<string>();
  for (const children of containers.values()) {
    const { visible } = cappedChildSlots(children.length, CHILD_RENDER_CAP);
    for (const c of children.slice(visible)) hiddenChildIds.add(c.id);
  }
  // Container parents are replaced by group boxes — map their ID to group ID
  const containerGroupId = new Map<string, string>();
  for (const parentId of containers.keys()) {
    containerGroupId.set(parentId, `group-${parentId}`);
  }

  // Resolve edge target/source: containers → group, container children → parent's group
  const resolveId = (id: string) => {
    if (containerGroupId.has(id)) return containerGroupId.get(id)!;
    if (childrenInContainer.has(id)) {
      const child = jobMap.get(id)!;
      return containerGroupId.get(child.parentJobId!) ?? id;
    }
    return id;
  };

  // Create edges for container parents (targeting group box)
  for (const [containerId, ] of containers) {
    const container = jobMap.get(containerId)!;
    const groupId = `group-${containerId}`;
    if (container.parentJobId && jobMap.has(container.parentJobId)) {
      edges.push({
        id: `p-${containerId}`,
        source: resolveId(container.parentJobId),
        target: groupId,
        type: 'smoothstep',
        style: { strokeWidth: 2 },
      });
    }
    if (container.spawnedByJobId && jobMap.has(container.spawnedByJobId)
      && container.spawnedByJobId !== container.parentJobId
      && !container.parentJobId) {
      edges.push({
        id: `s-${containerId}`,
        source: containerGroupId.get(container.spawnedByJobId) ?? container.spawnedByJobId,
        target: groupId,
        type: 'smoothstep',
        style: { strokeDasharray: '5,5', stroke: '#94a3b8', strokeWidth: 1 },
      });
    }
  }

  // Create job nodes (skip container parents — they become group boxes — and collapsed-away children)
  for (const job of jobs) {
    if (containers.has(job.id) || hiddenChildIds.has(job.id)) continue;

    nodes.push({
      id: job.id,
      type: 'traceNode',
      data: { ...job, highlighted: job.id === highlightId } as unknown as Record<string, unknown>,
      position: { x: 0, y: 0 },
    });

    if (job.parentJobId && jobMap.has(job.parentJobId)) {
      if (!childrenInContainer.has(job.id)) {
        edges.push({
          id: `p-${job.id}`,
          source: resolveId(job.parentJobId),
          target: job.id,
          type: 'smoothstep',
          style: { strokeWidth: 2 },
        });
      }
    }

    // SpawnedBy edge — use child ID directly so edge connects to the specific child inside the group
    if (job.spawnedByJobId && jobMap.has(job.spawnedByJobId)
      && job.spawnedByJobId !== job.parentJobId
      && !job.parentJobId
      && !childrenInContainer.has(job.id)) {
      edges.push({
        id: `s-${job.id}`,
        source: containerGroupId.get(job.spawnedByJobId) ?? job.spawnedByJobId,
        target: job.id,
        type: 'smoothstep',
        style: { strokeDasharray: '5,5', stroke: '#94a3b8', strokeWidth: 1 },
      });
    }
  }

  // Auto-layout with dagre — container children excluded (positioned manually inside group)
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir: 'LR', nodesep: 40, ranksep: 120 });

  // Add jobs to dagre — container parents get height for their group, container children excluded
  for (const job of jobs) {
    if (childrenInContainer.has(job.id)) continue; // positioned inside group, not by dagre
    const childCount = containers.get(job.id)?.length ?? 0;
    // Reserve height for the CAPPED render (visible children + one "+N more" slot), not the full child
    // count — otherwise dagre reserves thousands of rows for a large fan-out and blows the layout apart,
    // defeating the collapse. Derived from the same helper as the manual group-box sizing below.
    const { slots } = cappedChildSlots(childCount, CHILD_RENDER_CAP);
    const height = slots > 0
      ? slots * NODE_HEIGHT + (slots - 1) * CHILD_GAP + GROUP_PADDING * 2 + 30
      : NODE_HEIGHT;
    g.setNode(job.id, { width: NODE_WIDTH, height });
  }
  // Add edges — skip edges from containers to their children (they're positioned manually)
  for (const job of jobs) {
    if (childrenInContainer.has(job.id)) continue;
    if (job.parentJobId && jobMap.has(job.parentJobId)) {
      g.setEdge(job.parentJobId, job.id);
    }
  }
  // For spawned-by edges — redirect if source is a container child
  for (const job of jobs) {
    if (childrenInContainer.has(job.id)) continue;
    if (job.spawnedByJobId && jobMap.has(job.spawnedByJobId)
      && job.spawnedByJobId !== job.parentJobId
      && !job.parentJobId) {
      // If spawner is inside a container, use the container parent instead
      let source = job.spawnedByJobId;
      if (childrenInContainer.has(source)) {
        const spawner = jobMap.get(source)!;
        source = spawner.parentJobId!;
      }
      if (g.hasNode(source) && g.hasNode(job.id)) {
        g.setEdge(source, job.id);
      }
    }
  }

  dagre.layout(g);

  // Apply dagre positions to rendered nodes (skip container children — positioned manually)
  for (const node of nodes) {
    if (childrenInContainer.has(node.id)) continue;
    const pos = g.node(node.id);
    node.position = { x: pos.x - NODE_WIDTH / 2, y: pos.y - NODE_HEIGHT / 2 };
  }

  // Create group nodes — position children inside group at container parent's dagre position
  for (const [parentId, children] of containers) {
    const parent = jobMap.get(parentId)!;
    const { visible, hidden: hiddenCount } = cappedChildSlots(children.length, CHILD_RENDER_CAP);
    const visibleChildren = children.slice(0, visible);
    const childIds = visibleChildren.map(c => c.id);

    // Use container parent's dagre position as the group anchor
    const parentPos = g.node(parentId);
    const parentX = parentPos.x - NODE_WIDTH / 2;
    const parentY = parentPos.y;

    // Stack visible children vertically inside the group, plus one "+N more" slot when collapsed.
    const slotCount = childIds.length + (hiddenCount > 0 ? 1 : 0);
    const totalChildHeight = slotCount * NODE_HEIGHT + (slotCount - 1) * CHILD_GAP;
    const startY = parentY - totalChildHeight / 2;

    for (let i = 0; i < childIds.length; i++) {
      const childNode = nodes.find(n => n.id === childIds[i])!;
      childNode.position = { x: parentX, y: startY + i * (NODE_HEIGHT + CHILD_GAP) };
    }

    const summaryId = hiddenCount > 0 ? `summary-${parentId}` : null;
    if (summaryId) {
      nodes.push({
        id: summaryId,
        type: 'default',
        data: { label: `+${hiddenCount} more` },
        position: { x: parentX, y: startY + childIds.length * (NODE_HEIGHT + CHILD_GAP) },
        selectable: false,
        focusable: false,
        style: { width: NODE_WIDTH, height: NODE_HEIGHT, fontSize: 12, display: 'flex', alignItems: 'center', justifyContent: 'center' },
      });
    }

    const minX = parentX - GROUP_PADDING;
    const maxX = parentX + NODE_WIDTH + GROUP_PADDING;
    const minY2 = startY - GROUP_PADDING;
    const maxY = startY + totalChildHeight + GROUP_PADDING;

    const groupId = `group-${parentId}`;
    const label = `${shortId(parentId)} (${children.length} jobs)`;
    nodes.push({
      id: groupId,
      type: 'group',
      data: { label, id: parentId, kind: parent.kind, state: parent.currentState, highlighted: parentId === highlightId },
      position: { x: minX, y: minY2 },
      className: '!border-0 !p-0',
      selectable: false,
      focusable: false,
      style: { width: maxX - minX, height: maxY - minY2, zIndex: -1, backgroundColor: 'transparent', pointerEvents: 'all' },
    });

    // Make child nodes (and the summary node) relative to group
    const inGroup = summaryId ? [...childIds, summaryId] : childIds;
    for (const id of inGroup) {
      const node = nodes.find(n => n.id === id)!;
      node.parentId = groupId;
      node.position = { x: node.position.x - minX, y: node.position.y - minY2 };
    }
  }

  // React Flow requires parent nodes before children in the array
  nodes.sort((a, b) => (a.parentId ? 1 : 0) - (b.parentId ? 1 : 0));

  return { nodes, edges };
}

export default function TracePage() {
  const { traceId: rawTraceId, highlightId } = useParams<{ traceId: string; highlightId?: string }>();
  const [jobs, setJobs] = useState<TraceJobModel[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [hoveredEdge, setHoveredEdge] = useState<string | null>(null);

  // Normalize: accept both "4bf92f3577b34da6a3ce929d0e0e4736" and "4bf92f35-77b3-4da6-a3ce-929d0e0e4736"
  const traceId = rawTraceId && /^[0-9a-f]{32}$/i.test(rawTraceId)
    ? `${rawTraceId.slice(0,8)}-${rawTraceId.slice(8,12)}-${rawTraceId.slice(12,16)}-${rawTraceId.slice(16,20)}-${rawTraceId.slice(20)}`
    : rawTraceId;

  // Display without dashes (W3C format)
  const traceIdDisplay = traceId?.replace(/-/g, '') ?? '';

  useEffect(() => {
    if (traceId) {
      api.getTraceTree(traceId).then(setJobs).catch(() => setError('Unable to load trace'));
    }
  }, [traceId]);

  const { nodes, edges } = useMemo(() => {
    if (!jobs || jobs.length === 0) return { nodes: [], edges: [] };
    return buildGraph(jobs, highlightId);
  }, [jobs, highlightId]);

  // Highlight source/target nodes when hovering an edge
  const { styledNodes, styledEdges } = useMemo(() => {
    if (!hoveredEdge) return { styledNodes: nodes, styledEdges: edges };

    const edge = edges.find(e => e.id === hoveredEdge);
    if (!edge) return { styledNodes: nodes, styledEdges: edges };

    const connectedNodeIds = new Set([edge.source, edge.target]);

    // If edge connects to a group, highlight all children; if to a child, keep group visible but not siblings
    for (const nodeId of [...connectedNodeIds]) {
      const node = nodes.find(n => n.id === nodeId);
      if (node?.type === 'group') {
        for (const n of nodes) {
          if (n.parentId === nodeId) connectedNodeIds.add(n.id);
        }
      } else if (node?.parentId) {
        connectedNodeIds.add(node.parentId);
      }
    }

    return {
      styledNodes: nodes.map(n => ({
        ...n,
        style: {
          ...n.style,
          opacity: connectedNodeIds.has(n.id) ? 1 : 0.2,
          transition: 'opacity 0.2s',
        },
      })),
      styledEdges: edges.map(e => ({
        ...e,
        style: {
          ...e.style,
          opacity: e.id === hoveredEdge ? 1 : 0.1,
          strokeWidth: e.id === hoveredEdge ? 3 : (e.style?.strokeWidth ?? 2),
          transition: 'opacity 0.2s',
        },
      })),
    };
  }, [nodes, edges, hoveredEdge]);

  if (error) return <ErrorState message={error} />;
  if (!jobs) return <LoadingState />;

  return (
    <div>
      <div className="flex items-center gap-4 mb-4">
        <h1 className="text-2xl font-bold">Trace <span className="font-mono text-lg">{traceIdDisplay}</span></h1>
        <span className="text-sm text-muted-foreground">{jobs.length} jobs</span>
      </div>
      <div className="rounded-md border bg-card" style={{ height: 'calc(100vh - 12rem)' }}>
        <ReactFlow
          nodes={styledNodes}
          edges={styledEdges}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.2 }}
          minZoom={0.1}
          maxZoom={2}
          nodesConnectable={false}
          nodesDraggable={false}
          onEdgeMouseEnter={(_, edge) => setHoveredEdge(edge.id)}
          onEdgeMouseLeave={() => setHoveredEdge(null)}
          onNodeMouseEnter={() => setHoveredEdge(null)}
          onPaneClick={() => setHoveredEdge(null)}
          proOptions={{ hideAttribution: true }}
        >
          <Controls className="!bg-card !border !border-border !shadow-sm [&>button]:!bg-card [&>button]:!border-border [&>button]:!fill-foreground [&>button:hover]:!bg-accent" />
          <Background variant={BackgroundVariant.Dots} gap={16} size={1} />
          <div className="absolute top-3 right-3 bg-card border rounded-lg px-4 py-3 text-xs space-y-2 shadow-sm z-10">
            <div className="font-medium text-sm mb-2">Legend</div>
            <div className="flex items-center gap-2">
              <Briefcase className="h-3.5 w-3.5 text-muted-foreground" />
              <span>Job</span>
              <Mail className="h-3.5 w-3.5 text-muted-foreground ml-3" />
              <span>Message</span>
              <Layers className="h-3.5 w-3.5 text-muted-foreground ml-3" />
              <span>Batch</span>
            </div>
            <div className="flex items-center gap-2">
              <div className="w-8 h-0 border-t-2 border-foreground" />
              <span>Continuation</span>
            </div>
            <div className="flex items-center gap-2">
              <div className="w-8 h-0 border-t border-dashed border-muted-foreground" />
              <span>Spawned by</span>
            </div>
            <div className="flex items-center gap-2">
              <div className="w-8 h-4 border-2 border-dashed border-muted-foreground/30 rounded" />
              <span>Batch or Message</span>
            </div>
          </div>
        </ReactFlow>
      </div>
    </div>
  );
}
