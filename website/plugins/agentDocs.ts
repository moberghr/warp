import fs from "node:fs";
import path from "node:path";
import type { LoadContext, Plugin } from "@docusaurus/types";

// Makes the docs readable by agents without rendering the site:
//
//  1. Every doc page is also published as plain Markdown at its own URL plus `.md`
//     (/docs/features/sagas -> /docs/features/sagas.md), with MDX-only syntax removed and links
//     made absolute so the file stands on its own.
//  2. The build fails when `static/llms.txt` and the docs folder disagree: a page that is not
//     listed, or a listed page that no longer exists. llms.txt is written by hand (its one-line
//     descriptions are the point), so this is what keeps it from going stale.

interface DocPage {
  file: string;
  route: string; // e.g. "docs/features/sagas"
}

function findDocs(dir: string, docsDir: string): DocPage[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      return findDocs(full, docsDir);
    }
    if (!/\.mdx?$/.test(entry.name)) {
      return [];
    }
    const rel = path
      .relative(docsDir, full)
      .split(path.sep)
      .join("/")
      .replace(/\.mdx?$/, "")
      .replace(/(^|\/)index$/, "");

    return [{ file: full, route: rel ? `docs/${rel}` : "docs" }];
  });
}

function toMarkdown(source: string, page: DocPage, siteRoot: string): string {
  const pageDir = path.posix.dirname(`/${page.route}`);
  // An index page lives one level deeper on disk than its route says.
  const linkBase = /(^|[\\/])index\.mdx?$/.test(page.file)
    ? `/${page.route}`
    : pageDir;

  const absolute = (target: string): string => {
    if (/^([a-z]+:|#)/i.test(target)) {
      return target;
    }
    const [rawPath, hash] = target.split("#");
    const suffix = hash ? `#${hash}` : "";
    if (rawPath.startsWith("/img/")) {
      return `${siteRoot}${rawPath}${suffix}`;
    }
    const resolved = rawPath.startsWith("/")
      ? rawPath
      : path.posix.join(linkBase, rawPath);
    if (!resolved.startsWith("/docs")) {
      return `${siteRoot}${resolved}${suffix}`;
    }
    const route = resolved
      .replace(/\.mdx?$/, "")
      .replace(/\/index$/, "")
      .replace(/\/$/, "");

    return `${siteRoot}${route}.md${suffix}`;
  };

  // Drop the frontmatter: it is sidebar configuration, not content.
  const body = source.replace(/^---\n[\s\S]*?\n---\n/, "");

  // <Screenshot light=".." dark=".." alt=".." /> becomes an ordinary image.
  const withImages = body.replace(
    /<Screenshot\b([\s\S]*?)\/>/g,
    (_match, attrs: string) => {
      const light = /light="([^"]+)"/.exec(attrs)?.[1];
      const alt = /alt="([^"]*)"/.exec(attrs)?.[1] ?? "";

      return light ? `![${alt}](${light})` : "";
    },
  );

  let inFence = false;
  const lines = withImages.split("\n").flatMap((line) => {
    if (/^\s*(```|~~~)/.test(line)) {
      inFence = !inFence;

      return [line];
    }
    if (inFence) {
      return [line];
    }
    if (/^import\s.+from\s+['"].+['"];?\s*$/.test(line)) {
      return [];
    }

    return [
      line.replace(
        /\]\(([^)\s]+)\)/g,
        (_match, target: string) => `](${absolute(target)})`,
      ),
    ];
  });

  return `${lines
    .join("\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim()}\n`;
}

export default function agentDocs(context: LoadContext): Plugin {
  const docsDir = path.join(context.siteDir, "docs");
  const siteRoot = `${context.siteConfig.url}${context.baseUrl.replace(/\/$/, "")}`;

  return {
    name: "warp-agent-docs",

    async postBuild({ outDir }) {
      const pages = findDocs(docsDir, docsDir);

      for (const page of pages) {
        const target = path.join(outDir, `${page.route}.md`);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(
          target,
          toMarkdown(fs.readFileSync(page.file, "utf8"), page, siteRoot),
        );
      }

      const llms = fs.readFileSync(
        path.join(context.siteDir, "static", "llms.txt"),
        "utf8",
      );
      const listed = new Set(
        [...llms.matchAll(/\]\(([^)]+)\)/g)]
          .map((x) => x[1])
          .filter((x) => x.startsWith(`${siteRoot}/docs`))
          .map((x) => x.slice(siteRoot.length + 1).replace(/\.md$/, "")),
      );
      const routes = new Set(pages.map((x) => x.route));
      const unlisted = [...routes].filter((x) => !listed.has(x)).sort();
      const dead = [...listed].filter((x) => !routes.has(x)).sort();

      if (unlisted.length > 0 || dead.length > 0) {
        throw new Error(
          [
            "static/llms.txt is out of step with the docs folder.",
            ...unlisted.map((x) => `  not listed: ${siteRoot}/${x}.md`),
            ...dead.map((x) => `  listed but missing: ${siteRoot}/${x}.md`),
          ].join("\n"),
        );
      }
    },
  };
}
