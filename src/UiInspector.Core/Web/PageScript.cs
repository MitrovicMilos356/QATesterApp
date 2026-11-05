namespace UiInspector.Core.Web;

/// <summary>
/// JavaScript injected into the inspected page. <see cref="Locate"/> finds the element at a
/// viewport point (descending into same-origin iframes and open shadow roots) and stores
/// references in <c>window.__uiInspector</c>; the other snippets then count selector matches
/// against those references. Matching follows Playwright's rules: CSS pierces open shadow DOM.
/// </summary>
internal static class PageScript
{
    public const string State = "window.__uiInspector";

    /// <summary>Call as <c>Locate + "(x, y)"</c> with CSS-pixel viewport coordinates.</summary>
    public const string Locate = """
        ((x, y) => {
          const TEST_ATTRS = ['data-testid', 'data-test-id', 'data-test', 'data-qa', 'data-cy'];
          const KEPT_ATTRS = [...TEST_ATTRS, 'role', 'aria-label', 'name', 'type', 'placeholder', 'title', 'href', 'src'];

          // Playwright's order: an element, then its open shadow root, then its light children.
          const walk = (root, fn) => {
            const visit = (node) => {
              for (let c = node.firstElementChild; c; c = c.nextElementSibling) {
                fn(c);
                if (c.shadowRoot) visit(c.shadowRoot);
                visit(c);
              }
            };
            visit(root);
          };
          const deepQueryAll = (root, selector) => {
            const out = [];
            walk(root, (e) => { if (e.matches(selector)) out.push(e); });
            return out;
          };

          let doc = document, ox = 0, oy = 0, el = null, crossOrigin = false;
          const frames = [];
          let hosts = [];
          for (let guard = 0; guard < 10; guard++) {
            el = doc.elementFromPoint(x - ox, y - oy);
            while (el && el.shadowRoot) {
              const inner = el.shadowRoot.elementFromPoint(x - ox, y - oy);
              if (!inner || inner === el) break;
              hosts.push(el);
              el = inner;
            }
            if (el && (el.tagName === 'IFRAME' || el.tagName === 'FRAME')) {
              let inner = null;
              try { inner = el.contentDocument; } catch (e) { }
              frames.push(el);
              if (!inner) { crossOrigin = true; break; }
              const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
              ox += r.left + el.clientLeft + parseFloat(cs.paddingLeft);
              oy += r.top + el.clientTop + parseFloat(cs.paddingTop);
              doc = inner;
              hosts = [];
              continue;
            }
            break;
          }
          if (!el) return { found: false };

          const chain = [];
          for (let n = el; n && n.nodeType === 1 && chain.length < 25;) {
            chain.push(n);
            n = n.parentElement || (n.parentNode && n.parentNode.host) || null;
          }

          const describe = (e) => {
            const attrs = {};
            for (const a of KEPT_ATTRS) { const v = e.getAttribute(a); if (v !== null) attrs[a] = v; }
            return {
              tag: e.tagName.toLowerCase(),
              id: e.id || '',
              attrs,
              classes: [...e.classList],
              text: (e.innerText || e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 100),
              inShadow: e.getRootNode() instanceof ShadowRoot,
            };
          };

          // Elements that could carry an ARIA role: those with it implicitly (HTML-AAM), anything
          // with an explicit role attribute, and every element with the target's tag. The caller
          // confirms each one's computed role and name with Chromium's accessibility tree.
          const IMPLICIT = {
            button: 'button,input[type=button],input[type=submit],input[type=reset],input[type=image],summary',
            link: 'a[href],area[href]',
            textbox: 'input:not([type]),input[type=text],input[type=email],input[type=tel],input[type=url],textarea,[contenteditable]',
            searchbox: 'input[type=search]', checkbox: 'input[type=checkbox]', radio: 'input[type=radio]',
            combobox: 'select,input[list]', listbox: 'select[multiple],select[size],datalist', option: 'option',
            list: 'ul,ol,menu', listitem: 'li', navigation: 'nav', region: 'section', form: 'form', dialog: 'dialog',
            heading: 'h1,h2,h3,h4,h5,h6', img: 'img', table: 'table', row: 'tr', cell: 'td', columnheader: 'th',
            slider: 'input[type=range]', spinbutton: 'input[type=number]', main: 'main', banner: 'header',
            contentinfo: 'footer', complementary: 'aside', article: 'article', group: 'fieldset,details,optgroup',
          };
          const roleCandidates = (scope, role, tag) =>
            deepQueryAll(scope, [IMPLICIT[role], '[role]', tag].filter(Boolean).join(','));

          window.__uiInspector = { chain, frames, hosts, doc, deepQueryAll, roleCandidates, target: null };
          return {
            found: true,
            chain: chain.map(describe),
            frames: frames.map(describe),
            hosts: hosts.map(describe),
            crossOrigin,
            frameUrl: doc.URL,
          };
        })
        """;

    /// <summary>
    /// Counts matches of a CSS selector. <paramref name="scopeExpression"/> is relative to the
    /// state object (e.g. <c>u.doc</c>, <c>u.chain[3]</c>); deep = pierce open shadow roots
    /// (Playwright), shallow = the target's own root only (Selenium).
    /// </summary>
    public static string CountCss(string scopeExpression, string cssJsonLiteral, bool deep) => $$"""
        (() => {
          const u = {{State}};
          const scope = {{scopeExpression}};
          const m = {{(deep ? "u.deepQueryAll(scope, " + cssJsonLiteral + ")" : "[...scope.querySelectorAll(" + cssJsonLiteral + ")]")}};
          return { count: m.length, index: m.indexOf(u.target) };
        })()
        """;

    /// <summary>
    /// Short texts inside <c>chain[boxIndex]</c> that aren't part of the target: candidates for
    /// telling that container apart ("Order 1042" in one of many identical rows). Each text is
    /// taken from the smallest element that holds it, as Playwright's getByText does.
    /// </summary>
    public static string LandmarkTexts(int boxIndex, int targetIndex) => $$"""
        (() => {
          const u = {{State}};
          const box = u.chain[{{boxIndex}}], target = u.chain[{{targetIndex}}];
          const norm = (e) => (e.textContent || '').replace(/\s+/g, ' ').trim();
          const out = [];
          for (const e of box.querySelectorAll('*')) {
            if (e === target || e.contains(target) || target.contains(e)) continue;
            if (e.tagName === 'SCRIPT' || e.tagName === 'STYLE') continue;
            const text = norm(e);
            if (!text || text.length > 40 || out.includes(text)) continue;
            if ([...e.children].some(c => norm(c) === text)) continue;
            out.push(text);
            if (out.length >= 20) break;
          }
          return out;
        })()
        """;

    /// <summary>
    /// Checks a landmark text: how many smallest elements in the document have exactly that text,
    /// and, if one, how many ancestors with the container's tag lie between it and
    /// <c>chain[boxIndex]</c> (1 = the nearest, so XPath <c>ancestor::div[1]</c>). Uses the same
    /// XPath that the generated Selenium code does.
    /// </summary>
    public static string CheckLandmark(int boxIndex, string textJsonLiteral) => $$"""
        (() => {
          const u = {{State}};
          const box = u.chain[{{boxIndex}}], text = {{textJsonLiteral}};
          const lit = text.includes("'") ? 'concat(' + text.split("'").map(p => "'" + p + "'").join(`, "'", `) + ')' : "'" + text + "'";
          const xpath = '//*[normalize-space(.)=' + lit + ' and not(*[normalize-space(.)=' + lit + '])]';
          const r = u.doc.evaluate(xpath, u.doc, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
          if (r.snapshotLength !== 1) return { count: r.snapshotLength, level: -1 };
          let level = 0;
          for (let n = r.snapshotItem(0).parentElement; n; n = n.parentElement) {
            if (n.tagName === box.tagName) level++;
            if (n === box) return { count: 1, level };
          }
          return { count: 1, level: -1 };
        })()
        """;

    /// <summary>Counts matches of a CSS selector among a frame's siblings in its parent document.</summary>
    public static string CountFrameCss(int frameIndex, string cssJsonLiteral) => $$"""
        (() => {
          const u = {{State}};
          const f = u.frames[{{frameIndex}}];
          const m = [...f.ownerDocument.querySelectorAll({{cssJsonLiteral}})];
          return { count: m.length, index: m.indexOf(f) };
        })()
        """;

    /// <summary>Counts matches of a CSS selector for a shadow host within its own root (Selenium's view).</summary>
    public static string CountHostCss(int hostIndex, string cssJsonLiteral) => $$"""
        (() => {
          const u = {{State}};
          const h = u.hosts[{{hostIndex}}];
          const m = [...h.getRootNode().querySelectorAll({{cssJsonLiteral}})];
          return { count: m.length, index: m.indexOf(h) };
        })()
        """;
}
