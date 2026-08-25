import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';
import {visit} from 'unist-util-visit';

// Maps a top-level version-pattern heading (e.g. `## 0.12.0`) to an explicit
// id like `v0-12-0`. Without this, Docusaurus's slugifier would strip the
// dots and produce unreadable anchors like `#0120`. We can't use the
// `## 0.12.0 {#v0-12-0}` syntax because Docusaurus 3.10's stricter MDX
// expression parser rejects `{#...}` before the heading-id remark plugin
// sees it (acorn refuses `#v0-12-0` as a JS expression).
const versionAnchorPlugin = () => {
  return (tree: any) => {
    visit(tree, 'heading', (node: any) => {
      if (node.depth !== 2 || !node.children?.length) {
        return;
      }
      const text = node.children
        .filter((c: any) => c.type === 'text')
        .map((c: any) => c.value)
        .join('');
      const match = /^(\d+\.\d+(?:\.\d+)?)$/.exec(text.trim());
      if (!match) {
        return;
      }
      const id = 'v' + match[1].replace(/\./g, '-');
      node.data = node.data || {};
      node.data.hProperties = node.data.hProperties || {};
      node.data.hProperties.id = id;
      // Keep `id` on the AST itself so docusaurus's TOC extractor uses it.
      node.data.id = id;
    });
  };
};

const config: Config = {
  title: 'Warp',
  tagline: 'Distributed job processing and message queue for .NET',
  favicon: 'img/favicon.ico',

  future: {
    v4: true,
  },

  url: 'https://moberghr.github.io',
  baseUrl: '/warp/',

  organizationName: 'moberghr',
  projectName: 'warp',

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  headTags: [
    {
      tagName: 'link',
      attributes: {
        rel: 'alternate',
        type: 'text/plain',
        title: 'LLM-friendly docs',
        href: '/warp/llms.txt',
      },
    },
    {
      tagName: 'link',
      attributes: {
        rel: 'alternate',
        type: 'text/plain',
        title: 'LLM-friendly full reference',
        href: '/warp/llms-full.txt',
      },
    },
  ],

  // The dashboard docs lived under /docs/ui/ until the section was renamed and grouped to match the
  // dashboard's own nav. These keep every published URL working instead of 404ing a bookmark.
  plugins: [
    [
      '@docusaurus/plugin-client-redirects',
      {
        redirects: [
          { from: '/docs/ui/overview', to: '/docs/dashboard/overview' },
          { from: '/docs/ui/jobs', to: '/docs/dashboard/jobs' },
          { from: '/docs/ui/trace', to: '/docs/dashboard/trace' },
          { from: '/docs/ui/workers', to: '/docs/dashboard/workers' },
          { from: '/docs/ui/messages', to: '/docs/dashboard/workloads/messages' },
          { from: '/docs/ui/batches', to: '/docs/dashboard/workloads/batches' },
          { from: '/docs/ui/recurring', to: '/docs/dashboard/workloads/recurring' },
          { from: '/docs/ui/background-services', to: '/docs/dashboard/workloads/background-services' },
          { from: '/docs/ui/client', to: '/docs/dashboard/traffic/client' },
          { from: '/docs/ui/concurrency-limits', to: '/docs/dashboard/runtime/concurrency-limits' },
          { from: '/docs/ui/counters', to: '/docs/dashboard/health/counters' },
          { from: '/docs/ui/servers', to: '/docs/dashboard/health/applications' },
          // The Queues page was folded into the Counters page's Queues family.
          { from: '/docs/ui/queues', to: '/docs/dashboard/health/counters' },
        ],
      },
    ],
  ],

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/moberghr/warp/tree/main/website/',
          beforeDefaultRemarkPlugins: [versionAnchorPlugin],
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: 'img/screenshots/01-dashboard.png',
    colorMode: {
      respectPrefersColorScheme: false,
      disableSwitch: false,
    },
    docs: {
      sidebar: {
        autoCollapseCategories: true,
      },
    },
    navbar: {
      title: 'Warp',
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'tutorialSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          href: 'https://github.com/moberghr/warp',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            { label: 'Getting Started', to: '/docs/getting-started' },
            { label: 'Patterns', to: '/docs/patterns' },
            { label: 'Dashboard', to: '/docs/dashboard/overview' },
            { label: 'Releases', to: '/docs/releases' },
          ],
        },
        {
          title: 'More',
          items: [
            { label: 'GitHub', href: 'https://github.com/moberghr/warp' },
            { label: 'AI Docs (llms.txt)', href: 'pathname:///warp/llms.txt' },
            { label: 'AI Full Reference', href: 'pathname:///warp/llms-full.txt' },
          ],
        },
      ],
      copyright: `Copyright ${new Date().getFullYear()} Warp. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
