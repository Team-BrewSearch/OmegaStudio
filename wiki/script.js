(function () {
    'use strict';

    /* ------------------------------------------------------------------
       Site registry.

       Everything shared between pages lives here: page titles, subtitles,
       sidebar entries, meta descriptions and the generated index lists.
       A page's own .html file holds nothing but its content.

       To add a page:
         1. create the .html with the standard three-line skeleton
         2. add one entry to the game's `pages` array below

       To add a game: add one object to GAMES.
       ------------------------------------------------------------------ */

    const SITE = {
        brand: 'BrewSearch Reverse Engineering Wiki',
        sub: 'File formats and internals of the games we take apart',
        description: 'BrewSearch Reverse Engineering Wiki: file format documentation for games, checked against OmegaStudio.',
        icon: '\u{1F4D6}',
        themeColor: '#0e1a2b'
    };

    const GAMES = [
        {
            name: 'Disgaea DS',
            dir: 'DisgaeaDS',
            home: 'disgaeads.html',
            sub: 'Nintendo DS file formats',
            blurb: 'audio archives, Nintendo sound formats, images, maps and the ROM tree',
            pages: [
                { file: 'archive/dsarcfl.html', label: 'DSARC FL', tag: 'archive',
                  sub: 'Named-file archive',
                  blurb: 'the named-file archive behind every audio .dat' },
                { file: 'archive/dseq.html', label: 'DSEQ', tag: 'music',
                  sub: 'Music container (.msnd)',
                  blurb: 'one piece of music: an SSEQ, its SBNK and its SWAR' },
                { file: 'audio/sbnk.html', label: 'SBNK', tag: 'bank',
                  sub: 'Instrument bank',
                  blurb: 'maps instruments and key ranges to SWAR samples' },
                { file: 'audio/sseq.html', label: 'SSEQ', tag: 'sequence',
                  sub: 'Music sequence',
                  blurb: 'compiled note and control commands for all music' },
                { file: 'audio/strm.html', label: 'STRM', tag: 'stream',
                  sub: 'Streamed audio',
                  blurb: 'streamed audio, used only in msgvo.dat' },
                { file: 'audio/swar.html', label: 'SWAR', tag: 'wave archive',
                  sub: 'Wave archive',
                  blurb: 'the sample set an SBNK plays from' },
                { file: 'audio/swav.html', label: 'SWAV', tag: 'wave',
                  sub: 'Single wave sample',
                  blurb: 'one PCM or IMA-ADPCM sample: sound effects and voice clips' },
                { file: 'compression/imy.html', label: 'IMY', tag: 'image',
                  sub: 'Compressed image',
                  blurb: 'compressed images for nearly every texture and screen' },
                { file: 'map/mpd.html', label: 'MPD', tag: 'map',
                  sub: 'Tile-based map',
                  blurb: 'the tile-based map format' },
                { file: 'map/mpds.html', label: 'MPDS', tag: 'compressed',
                  sub: 'Compressed MPD map',
                  blurb: 'compressed MPD, used by all 198 maps' }
            ]
        },
        {
            name: 'DreamWorks Super Star Kartz',
            dir: 'DWSSK',
            home: 'dwssk.html',
            sub: 'PlayStation 3, Xbox 360, Wii and 3DS file formats',
            blurb: 'WAD containers, textures, models, animation, collision, navigation, level databases and data tables',
            pages: [
                { file: 'wad/wad.html', label: 'WAD', tag: 'container',
                  sub: 'HIG! container with hash buckets and typed entries',
                  blurb: 'the HIG! container: header, TJZIP image, hash buckets, entry types and lookup' },
                { file: 'textures/htx.html', label: 'Textures', tag: 'texture',
                  sub: 'HTX header, pixel formats and platform layouts',
                  blurb: 'the .htx header, its pixel formats and each platform\'s storage' },
                { file: 'models/gd.html', label: 'Models', tag: 'model',
                  sub: 'Shared header, materials and per-kind mesh layouts',
                  blurb: 'gdw world, gdi instanced, gdt tie and gds skinned models, with vertex formats and Wii display lists' },
                { file: 'models/ske.html', label: 'Skeletons', tag: 'skeleton',
                  sub: 'Bones, rest poses and the skinning palette',
                  blurb: 'the per-LOD skeleton in .gds files and how the skinning palette is built' },
                { file: 'anim/animpack.html', label: 'AnimPack', tag: 'animation',
                  sub: 'Character clips with bone tracks',
                  blurb: 'animation packs: clips, tracks, packed keys, curves and sampling' },
                { file: 'collision/col.html', label: 'Collision', tag: 'collision',
                  sub: 'Collision objects, meshes and surface types',
                  blurb: '.col objects and meshes, and the .csd surface types they reference' },
                { file: 'level/bog.html', label: 'Level DB', tag: 'level',
                  sub: 'BOG level database and placement tables',
                  blurb: 'the .bog level database: lights, entities, triggers, splines, ties and shrubs' },
                { file: 'level/nav.html', label: 'NavMesh', tag: 'navigation',
                  sub: 'AI navigation meshes',
                  blurb: 'navigation meshes with polygons, edges and bounds' },
                { file: 'data/p2b.html', label: 'Tuning', tag: 'data',
                  sub: 'Self-describing P2B tuning tables',
                  blurb: 'tuning tables carrying their own types, enums and strings' },
                { file: 'data/lcl.html', label: 'Strings', tag: 'text',
                  sub: 'LCL string tables and NOG name tables',
                  blurb: 'localised .lcl string tables with hashed records, and .nog name lists' },
                { file: 'ui/fkd.html', label: 'Fonts', tag: 'font',
                  sub: 'FKD font kerning data',
                  blurb: 'glyph cells and advances for a font texture' },
                { file: 'ui/cam.html', label: 'Cameras', tag: 'camera',
                  sub: 'Camera path keyframes',
                  blurb: 'camera paths with position, direction and field-of-view keys' }
            ]
        },
        {
            name: 'Polarbit Racing Engine',
            dir: 'Polarbit',
            home: 'polarbit.html',
            sub: 'Mobile racing engine file formats',
            blurb: 'VFS archives, compression, textures, fonts, PIFF and PXC models, audio and string tables',
            pages: [
                { file: 'vfs/fufs.html', label: 'FUFS', tag: 'container',
                  sub: 'VFS archive with a hash-sorted entry table',
                  blurb: 'the FUFS container: header, entry table, name tables and name hash' },
                { file: 'vfs/compression.html', label: 'Compression', tag: 'compression',
                  sub: 'PLZP and bare zlib layers',
                  blurb: 'PLZP and bare zlib layers, with the original compressor settings' },
                { file: 'texture/pvr.html', label: 'PVR', tag: 'texture',
                  sub: 'PowerVR legacy textures',
                  blurb: 'PVR v2 textures: header, pixel formats, mipmaps and twiddled storage' },
                { file: 'texture/font.html', label: 'PFNT', tag: 'font',
                  sub: 'Bitmap fonts with glyph tables',
                  blurb: 'PFNT bitmap fonts: header, glyph records, atlas and pixel encodings' },
                { file: 'texture/plates.html', label: 'Plates', tag: 'atlas',
                  sub: 'TLPB and PLT atlas plates',
                  blurb: 'TLPB (Reckless Getaway) and PLT (Raging Thunder 2) atlas sub-image tables' },
                { file: 'model/piff.html', label: 'PIFF', tag: 'model',
                  sub: 'Chunked models and tracks (3DOB, SCEN)',
                  blurb: 'PIFF chunk container: 3DOB models with skinning, animation and LODs, and SCEN tracks' },
                { file: 'model/pxc.html', label: 'PXC', tag: 'scene',
                  sub: 'Object trees, geometry, curves and collision',
                  blurb: 'PXC scene files: object tree, properties, geometry, curves, collision and cells' },
                { file: 'audio/audio.html', label: 'Audio', tag: 'audio',
                  sub: 'PSND sounds and PMOD modules',
                  blurb: 'PSND sound blocks and PMOD tracker modules' },
                { file: 'data/strings.html', label: 'Strings', tag: 'text',
                  sub: 'LANG, FCOL, locale sources, XML and shaders',
                  blurb: 'string tables and text: LANG, FCOL, CSV and HXX locale sources, XML, GLSL shaders and credits' }
            ]
        },
        {
            name: 'Suzumiya Haruhi no Chourantou / Gekitou',
            dir: 'Harutou',
            home: 'harutou.html',
            sub: 'Windows (Direct3D 9) file formats',
            blurb: 'BIN archives, textures, skinned models, animation, audio and data',
            pages: [
                { file: 'archive/bin.html', label: 'BIN', tag: 'archive',
                  sub: 'Nested archive container (data/*.bin)',
                  blurb: 'the nested table of contents behind every <code>data/*.bin</code> file' },
                { file: 'texture/ringtex2.html', label: 'RINGTEX2', tag: 'texture',
                  sub: 'Uncompressed RGBA texture',
                  blurb: '64-byte header and raw RGBA8 pixels' },
                { file: 'model/model.html', label: 'Model', tag: 'model',
                  sub: 'Skinned model blob',
                  blurb: 'node list of bones and meshes, materials, draw stream and embedded textures' },
                { file: 'animation/anim.html', label: 'Animation', tag: 'animation',
                  sub: 'Keyframed animation clip',
                  blurb: 'one keyframed clip per blob, matched to a model by node name' },
                { file: 'audio/audio.html', label: 'Audio', tag: 'audio',
                  sub: 'Ogg Vorbis and RIFF WAVE payloads',
                  blurb: 'standard Ogg Vorbis and RIFF WAVE files, no custom container' },
                { file: 'effect/btef.html', label: 'BTEF', tag: 'effect',
                  sub: 'Effect script blob',
                  blurb: 'effect scripts, not yet reverse engineered' },
                { file: 'data/tables.html', label: 'Data tables', tag: 'tables',
                  sub: 'Gameplay tables, shader bytecode and UI data',
                  blurb: 'gameplay tables, shader bytecode and UI data, not yet reverse engineered' }
            ]
        },
        {
            name: 'Virtuos DS Racing Engine',
            dir: 'Kartz',
            home: 'kartz.html',
            sub: 'Nintendo DS racing engine file formats',
            blurb: 'archives, resource databases, textures, tracks, collision and models for five Nintendo DS racing games',
            pages: [
                { file: 'archive/gda.html', label: 'GDA', tag: 'archive',
                  sub: 'Archive triple: GDA, GDF and GDT',
                  blurb: 'three-file archive: data, path table and range table' },
                { file: 'resource/rdf.html', label: 'RDF', tag: 'resources',
                  sub: 'Resource database and texture slot table',
                  blurb: 'the serialised object tree, and the texture slot table a TRK indexes' },
                { file: 'resource/raf.html', label: 'RAF', tag: 'relocations',
                  sub: 'Pointer fixup table for an RDF',
                  blurb: 'the pointer offsets an RDF needs patched at load time' },
                { file: 'texture/5tx.html', label: '5TX', tag: 'texture',
                  sub: 'NTTX texture container',
                  blurb: 'Nintendo DS textures with PALT and IMGE sections, and how tracks bind them' },
                { file: 'track/trk.html', label: 'TRK', tag: 'track',
                  sub: 'Track geometry container',
                  blurb: 'track geometry: header, chunk tables, chunks and tail sections' },
                { file: 'track/displaylist.html', label: 'Display list', tag: 'geometry',
                  sub: 'Nintendo DS GX command stream inside a chunk',
                  blurb: 'the GX opcode stream a chunk carries, and the shape real chunks use' },
                { file: 'track/col.html', label: 'COL', tag: 'collision',
                  sub: 'Collision surface layout and sector records',
                  blurb: 'the cross-section chain and sector records of the drivable surface' },
                { file: 'track/bpvs.html', label: 'BPVS', tag: 'visibility',
                  sub: 'Chunk visibility run lists',
                  blurb: 'per-sector run lists of the chunks to draw' },
                { file: 'data/zon.html', label: 'ZON', tag: 'scenery',
                  sub: 'Scenery bundle layout',
                  blurb: 'scenery models built from track-format chunks' },
                { file: 'data/car.html', label: 'CAR', tag: 'vehicle',
                  sub: 'Vehicle and prop model layout',
                  blurb: 'rigid models, their chunk table and their wheel block' },
                { file: 'data/amd.html', label: 'AMD', tag: 'character',
                  sub: 'Character model layout and skinning',
                  blurb: 'skinned models, their bone table and their display-list skin' },
                { file: 'data/ske.html', label: 'SKE', tag: 'skeleton',
                  sub: 'Skeleton layout',
                  blurb: 'parented bones and their rest transforms' },
                { file: 'data/ani.html', label: 'ANI', tag: 'animation',
                  sub: 'Animation clip layout',
                  blurb: 'per-bone matrix tracks and frame poses' },
                { file: 'data/other.html', label: 'Editing', tag: 'guide',
                  sub: 'Replacing assets, round trips and known limits',
                  blurb: 'what OmegaStudio can replace, track editing rules, round-trip results and known limits' }
            ]
        }
    ];

    /* ------------------------------------------------------------------
       Derived page table. `path` is always relative to the wiki root.
       ------------------------------------------------------------------ */

    const HOME = {
        path: 'index.html',
        title: SITE.brand,
        heading: SITE.brand,
        sub: SITE.sub,
        description: SITE.description,
        list: 'games'
    };

    const PAGES = [HOME];

    GAMES.forEach(function (game) {
        game.path = game.dir + '/' + game.home;

        PAGES.push({
            path: game.path,
            game: game,
            title: 'File Formats \u00B7 ' + game.name + ' Wiki',
            heading: 'Home',
            sub: game.sub,
            description: game.name + ' file format reference: ' +
                game.pages.map(function (page) { return page.label; }).join(', ') + '.',
            list: 'formats'
        });

        game.pages.forEach(function (page) {
            page.path = game.dir + '/' + page.file;
            page.game = game;
            page.title = page.label + ' \u00B7 ' + game.name + ' Wiki';
            page.heading = page.label;
            page.description = page.label + ' - ' + stripTags(page.blurb) +
                '. File format documentation for ' + game.name + '.';
            PAGES.push(page);
        });
    });

    function stripTags(html) {
        return html.replace(/<[^>]*>/g, '');
    }

    /* ------------------------------------------------------------------
       Location. Every generated link is absolute, resolved against the
       directory this script was loaded from, so page depth never matters.
       ------------------------------------------------------------------ */

    const src = document.currentScript.src;
    const ROOT = src.slice(0, src.lastIndexOf('/') + 1);

    function url(path) {
        return ROOT + path;
    }

    function currentPath() {
        const here = location.href.split('#')[0].split('?')[0];
        if (here.indexOf(ROOT) !== 0) return '';

        const rel = decodeURIComponent(here.slice(ROOT.length));
        return rel === '' || rel.slice(-1) === '/' ? rel + 'index.html' : rel;
    }

    const path = currentPath();
    const page = PAGES.filter(function (entry) { return entry.path === path; })[0] || null;

    /* ------------------------------------------------------------------
       Head. Written while the head is still parsing. A script-inserted
       stylesheet does not block rendering, so the page stays hidden until
       both the stylesheet and the DOM are ready (see the end of this file).
       ------------------------------------------------------------------ */

    function element(tag, attributes) {
        const node = document.createElement(tag);
        Object.keys(attributes).forEach(function (name) {
            node.setAttribute(name, attributes[name]);
        });
        return node;
    }

    function buildHead() {
        const favicon = 'data:image/svg+xml,' + encodeURIComponent(
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">' +
            '<text y=".9em" font-size="90">' + SITE.icon + '</text></svg>'
        );

        document.head.appendChild(element('meta', {
            name: 'viewport',
            content: 'width=device-width, initial-scale=1.0'
        }));
        document.head.appendChild(element('meta', {
            name: 'theme-color',
            content: SITE.themeColor
        }));
        document.head.appendChild(element('meta', {
            name: 'description',
            content: page ? page.description : SITE.description
        }));
        document.head.appendChild(element('link', { rel: 'icon', href: favicon }));

        const sheet = element('link', { rel: 'stylesheet', href: url('style.css') });
        sheet.onload = sheet.onerror = ready;
        document.head.appendChild(sheet);

        document.title = page ? page.title : SITE.brand;
    }

    /* ------------------------------------------------------------------
       Sidebar.
       ------------------------------------------------------------------ */

    function sidebarItem(entry, label, tag) {
        const active = entry.path === path;

        return '<a class="sidebar-item' + (active ? ' active' : '') + '"' +
            ' href="' + url(entry.path) + '"' +
            (active ? ' aria-current="page"' : '') + '>' +
            '<span class="item-label">' + label + '</span>' +
            (tag ? '<span class="item-tag">' + tag + '</span>' : '') +
            '</a>';
    }

    function buildSidebar() {
        const sections = GAMES.map(function (game) {
            const items = [sidebarItem(game, 'Home')].concat(
                game.pages.map(function (entry) {
                    return sidebarItem(entry, entry.label, entry.tag);
                })
            );

            return '<div class="sidebar-section">' +
                '<div class="sidebar-section-title">' + game.name + '</div>' +
                items.join('') +
                '</div>';
        });

        return '<a class="skip-link" href="#main">Skip to content</a>' +
            '<button class="sidebar-toggle" id="sidebarToggle" aria-label="Toggle navigation">' +
            '<span class="menu-icon">\u2630</span><span class="close-icon">\u2715</span></button>' +
            '<div class="sidebar-overlay" id="sidebarOverlay"></div>' +
            '<nav class="sidebar" id="sidebar" role="navigation" aria-label="Main navigation">' +
            '<div class="sidebar-brand"><a href="' + url(HOME.path) + '">' +
            '<span class="brand-icon">' + SITE.icon + '</span>' +
            '<span>' + SITE.brand + '</span>' +
            '</a></div>' +
            '<div class="sidebar-nav">' + sections.join('') + '</div>' +
            '</nav>';
    }

    /* ------------------------------------------------------------------
       Generated index lists. A page opts in with <ul data-list></ul>;
       which list it gets follows from the page itself.
       ------------------------------------------------------------------ */

    function listItems() {
        if (!page) return [];

        if (page.list === 'games') {
            return GAMES.map(function (game) {
                return '<a href="' + url(game.path) + '">' + game.name + '</a> - ' + game.blurb;
            });
        }

        if (page.list === 'formats') {
            return page.game.pages.map(function (entry) {
                return '<a href="' + url(entry.path) + '">' + entry.label + '</a> - ' + entry.blurb;
            });
        }

        return [];
    }

    function fillLists() {
        const items = listItems();

        [].forEach.call(document.querySelectorAll('[data-list]'), function (node) {
            node.innerHTML = items.map(function (item) {
                return '<li>' + item + '</li>';
            }).join('');
        });
    }

    /* ------------------------------------------------------------------
       Page shell. The body of a page file is its content and nothing
       else; the header, the badge and the wrappers are added here.
       ------------------------------------------------------------------ */

    function buildPage() {
        const content = document.createElement('div');
        content.className = 'page-content';

        while (document.body.firstChild) {
            content.appendChild(document.body.firstChild);
        }

        if (page && page.tag) {
            const first = content.querySelector('p');
            if (first) {
                first.insertAdjacentHTML('afterbegin',
                    '<span class="badge">' + page.tag + '</span> ');
            }
        }

        const header = document.createElement('div');
        header.className = 'page-header';
        header.innerHTML =
            '<h1>' + (page ? page.heading : document.title) + '</h1>' +
            (page && page.sub ? '<div class="page-sub">' + page.sub + '</div>' : '');

        const wrapper = document.createElement('div');
        wrapper.id = 'content';
        wrapper.appendChild(header);
        wrapper.appendChild(content);

        const main = document.createElement('main');
        main.className = 'main';
        main.id = 'main';
        main.setAttribute('role', 'main');
        main.setAttribute('tabindex', '-1');
        main.appendChild(wrapper);

        document.body.appendChild(main);
        document.body.insertAdjacentHTML('afterbegin', buildSidebar());

        fillLists();
    }

    /* ------------------------------------------------------------------
       Sidebar behaviour (mobile).
       ------------------------------------------------------------------ */

    function closeSidebar() {
        document.body.classList.remove('sidebar-open');
    }

    document.addEventListener('click', function (event) {
        if (event.target.closest('#sidebarToggle')) {
            document.body.classList.toggle('sidebar-open');
        }
        if (event.target.id === 'sidebarOverlay') {
            closeSidebar();
        }
    });

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            closeSidebar();
        }
    });

    /* ------------------------------------------------------------------
       Start-up. The page is built once the stylesheet and the DOM are both
       ready, so it never paints unstyled or animates into place.
       ------------------------------------------------------------------ */

    let pending = 2;

    function ready() {
        if (--pending) return;
        buildPage();
        document.documentElement.style.visibility = '';
    }

    document.documentElement.style.visibility = 'hidden';
    buildHead();

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', ready);
    } else {
        ready();
    }
})();
