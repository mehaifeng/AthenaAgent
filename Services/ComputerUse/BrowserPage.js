globalThis.athenaPage ??= (() => {
    let host, cursor, label, point;
    let controls = [];

    const viewport = () => ({
        width: innerWidth, height: innerHeight, scrollX, scrollY, url: location.href
    });

    function observe() {
        const state = { ...viewport(), title: document.title, ready: !!document.body && document.readyState !== 'loading' };
        controls = [];
        const visible = [];
        const targets = 'a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=combobox],[contenteditable=true]';
        for (const element of document.querySelectorAll(targets)) {
            const rect = element.getBoundingClientRect();
            const left = Math.max(0, rect.left);
            const right = Math.min(innerWidth, rect.right);
            const top = Math.max(0, rect.top);
            const bottom = Math.min(innerHeight, rect.bottom);
            if (left >= right || top >= bottom || element.matches(':disabled,[aria-disabled=true],input[type=password]'))
                continue;
            const x = (left + right) / 2;
            const y = (top + bottom) / 2;
            if (!element.contains(document.elementFromPoint(x, y)))
                continue;
            const caption = element.localName === 'select' ? element.selectedOptions[0]?.text : element.innerText;
            const label = (element.getAttribute('aria-label') || element.labels?.[0]?.innerText ||
                element.getAttribute('placeholder') || caption || element.getAttribute('title') ||
                element.name || element.id || '').trim().replace(/\s+/g, ' ');
            if (!label)
                continue;
            const role = element.getAttribute('role') || (element.localName === 'input' ? element.type + ' field' : element.localName);
            controls.push(element);
            visible.push({ label: label.slice(0, 100), role, x, y });
            if (visible.length === 45)
                break;
        }

        const content = document.querySelector('main') || document.body;
        const snippets = [];
        let length = 0;
        for (const element of content?.querySelectorAll('h1,h2,h3,p,li,th,td') || []) {
            const rect = element.getBoundingClientRect();
            if (rect.width === 0 || rect.height === 0 || rect.bottom <= 0 || rect.top >= innerHeight)
                continue;
            const snippet = (element.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 420);
            if (!snippet)
                continue;
            snippets.push(snippet);
            length += snippet.length;
            if (length >= 2300)
                break;
        }
        return { ...state, controls: visible, visibleText: snippets.join('\n').slice(0, 2300) };
    }

    function create(parent, tag, style) {
        const element = document.createElement(tag);
        element.style.cssText = style;
        parent.append(element);
        return element;
    }

    function mount() {
        if (host?.isConnected)
            return;
        host = create(document.documentElement, 'div',
            'all:initial!important;position:fixed!important;inset:0!important;z-index:2147483647!important;pointer-events:none!important');
        host.setAttribute('aria-hidden', 'true');
        host.setAttribute('data-athena-computer-use', '');
        const shadow = host.attachShadow({ mode: 'closed' });
        cursor = create(shadow, 'div',
            'position:absolute;left:0;top:0;pointer-events:none;filter:drop-shadow(0 2px 5px #0a1f2788)');
        create(cursor, 'span',
            'position:absolute;width:23px;height:28px;background:#09242a;clip-path:polygon(0 0,100% 61%,54% 64%,32% 100%)');
        create(cursor, 'span',
            'position:absolute;left:3px;top:4px;width:16px;height:20px;background:#65f2cd;clip-path:polygon(0 0,100% 61%,54% 64%,32% 100%)');
        label = create(cursor, 'span',
            'position:absolute;left:21px;top:21px;border-radius:6px;padding:4px 7px;white-space:nowrap;' +
            'background:#09242a;color:#dbfff4;font:600 12px system-ui;letter-spacing:.1px');
        label.textContent = 'Athena';
        point = [Math.min(innerWidth * .15, 120), Math.min(innerHeight * .15, 96)];
    }

    const translate = ([x, y]) => 'translate(' + x + 'px,' + y + 'px)';

    async function move(x, y) {
        mount();
        const destination = [x, y];
        const motion = cursor.animate(
            [{ transform: translate(point) }, { transform: translate(destination) }],
            { duration: 310, easing: 'cubic-bezier(.17,.75,.28,1)', fill: 'forwards' });
        await motion.finished;
        cursor.style.transform = translate(destination);
        motion.cancel();
        point = destination;
    }

    async function clickPulse() {
        const ring = create(cursor, 'span',
            'position:absolute;left:0;top:0;width:20px;height:20px;box-sizing:border-box;border:2px solid #65f2cd;' +
            'border-radius:50%;box-shadow:0 0 0 1px #09242a;transform:translate(-50%,-50%);pointer-events:none');
        ring.animate([{ scale: .55, opacity: 1 }, { scale: 2.25, opacity: 0 }], 340).finished.then(() => ring.remove());
        // Paint the pulse before a click that may replace the document.
        await new Promise(resolve => setTimeout(resolve, 105));
    }

    function hasEditableFocus() {
        const active = document.activeElement;
        return !!(active?.isContentEditable || active?.matches(
            'textarea:not([readonly]),input:not([readonly]):is([type=text],[type=search],[type=email],[type=url],[type=tel],[type=number],:not([type]))'));
    }

    return {
        viewport, observe, move, clickPulse, hasEditableFocus,
        hits: (index, x, y) => index === null || !!controls[index - 1]?.contains(document.elementFromPoint(x, y)),
        typing: active => { label.textContent = active ? 'Athena · Typing' : 'Athena'; },
        remove: () => { host?.remove(); host = null; }
    };
})();
