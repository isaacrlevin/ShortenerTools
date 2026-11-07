// Click map for the Statistics page. Leaflet (wwwroot/lib/leaflet, loaded globally as L) draws one circle per place,
// sized by clicks. Place names come from request headers, so they are only ever set as text, never as HTML.
const maps = new WeakMap();

const CITY_COLOR = '#2a78d6';      // categorical slot 1
const COUNTRY_COLOR = '#eb6834';   // categorical slot 2: country-centre placement (validated pair with slot 1)
const SURFACE = '#fcfcfb';         // 2px ring so overlapping circles stay distinct
const HOVER_RING = '#0b0b0b';
const MIN_RADIUS = 5;              // >= 8px diameter even for a single click
const MAX_RADIUS = 24;

function radiusFor(clicks, maxClicks) {
    // Area, not radius, is proportional to clicks.
    return MIN_RADIUS + (MAX_RADIUS - MIN_RADIUS) * Math.sqrt(clicks / maxClicks);
}

function tooltipContent(point) {
    const box = document.createElement('div');
    const place = document.createElement('strong');
    place.textContent = point.label;
    const count = document.createElement('div');
    count.textContent = `${point.clicks.toLocaleString()} click${point.clicks === 1 ? '' : 's'}`;
    box.append(place, count);
    return box;
}

function legendEntry(color, text) {
    const row = document.createElement('div');
    row.className = 'click-map-legend-row';
    const swatch = document.createElement('span');
    swatch.className = 'click-map-legend-swatch';
    swatch.style.background = color;
    const label = document.createElement('span');
    label.textContent = text;
    row.append(swatch, label);
    return row;
}

function createLegend() {
    const legend = L.control({ position: 'bottomleft' });
    legend.onAdd = () => {
        const box = L.DomUtil.create('div', 'click-map-legend');
        box.append(
            legendEntry(CITY_COLOR, 'City'),
            legendEntry(COUNTRY_COLOR, 'Country only (older clicks)'));
        const note = document.createElement('div');
        note.className = 'click-map-legend-note';
        note.textContent = 'Circle size = clicks';
        box.append(note);
        return box;
    };
    return legend;
}

function getOrCreateMap(element) {
    let state = maps.get(element);
    if (state) return state;

    const map = L.map(element, { worldCopyJump: true, minZoom: 1, zoomSnap: 0.5 });
    // Standard OpenStreetMap tiles (attribution required; https://operations.osmfoundation.org/policies/tiles/).
    // app.css desaturates them so the data circles carry the colour.
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
    }).addTo(map);
    createLegend().addTo(map);
    state = { map, layer: L.layerGroup().addTo(map) };
    maps.set(element, state);
    return state;
}

export function render(element, points) {
    const { map, layer } = getOrCreateMap(element);
    layer.clearLayers();
    const maxClicks = Math.max(1, ...points.map(p => p.clicks));

    // Points arrive largest first, so smaller circles are drawn on top and stay hoverable.
    for (const point of points) {
        const color = point.countryOnly ? COUNTRY_COLOR : CITY_COLOR;
        const style = { color: SURFACE, weight: 2, fillColor: color, fillOpacity: 0.6 };
        const circle = L.circleMarker([point.latitude, point.longitude],
            { ...style, radius: radiusFor(point.clicks, maxClicks) });
        circle.bindTooltip(tooltipContent(point), { direction: 'top', offset: [0, -4] });
        circle.on('mouseover', () => circle.setStyle({ color: HOVER_RING, fillOpacity: 0.8 }));
        circle.on('mouseout', () => circle.setStyle(style));
        layer.addLayer(circle);
    }

    map.invalidateSize();
    if (points.length === 0) {
        map.setView([20, 0], 1.5);
    } else {
        map.fitBounds(L.latLngBounds(points.map(p => [p.latitude, p.longitude])),
            { padding: [40, 40], maxZoom: 6 });
    }
}

export function dispose(element) {
    const state = maps.get(element);
    if (state) {
        state.map.remove();
        maps.delete(element);
    }
}
