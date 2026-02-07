# Map Preview Guide

The Map Preview feature provides an interactive map interface for viewing your published layers, testing styles, and exploring spatial data. Use it to verify data quality, test styling, and share map previews with others.

## 📋 **Prerequisites**

- At least one [published layer](layers-guide.md) with vector tile support
- Optional: Custom [map styles](styles-guide.md) for enhanced visualization
- Modern browser with WebGL support

## 🎯 **Preview Capabilities**

- **Interactive Maps**: Pan, zoom, and explore your data
- **Layer Management**: Toggle layer visibility and opacity
- **Style Testing**: Preview custom styles before deployment
- **Data Inspection**: Click features to view attributes
- **Multi-Format Support**: Vector tiles, raster tiles, and WMS overlays

---

## **Step 1: Access Map Preview**

Open the interactive map viewer:

1. Navigate to Admin UI at `/admin`
2. Click **🗺️ Preview** in the sidebar
3. Interactive map loads with available layers

*🖼️ Screenshot needed: Preview page showing interactive map with layer panel*

---

## **Step 2: Map Interface Overview**

### **Map Controls:**

*🖼️ Screenshot needed: Map interface with all controls labeled*

**Navigation Controls:**
- **Zoom In/Out**: `+` and `-` buttons or mouse wheel
- **Pan**: Click and drag to move around
- **Full Screen**: Expand map to full browser window
- **Home**: Reset to initial view extent

**Layer Panel:**
- **Layer List**: All available published layers
- **Visibility Toggle**: Show/hide individual layers
- **Opacity Slider**: Adjust layer transparency
- **Style Selector**: Choose different styles per layer

**Information Panel:**
- **Layer Details**: Metadata about selected layers
- **Feature Inspector**: Attribute data from clicked features
- **Coordinates**: Current cursor position
- **Scale**: Current map scale

---

## **Step 3: Adding and Managing Layers**

### **Add Layers to Map**

1. Click **📂 Add Layer** button in layer panel
2. Browse available published layers
3. Select layers to display on map
4. Configure layer display settings

*🖼️ Screenshot needed: Add layer dialog with published layer selection*

### **Layer Configuration:**

**Display Settings:**
- **Name**: Display name in layer list
- **Visibility**: Initially visible on map load
- **Opacity**: Default transparency level (0-100%)
- **Z-Index**: Rendering order (higher numbers on top)

**Style Assignment:**
- **Default Style**: Built-in styling for layer data
- **Custom Style**: Apply styles created in style editor
- **External Style**: Use MapLibre/Mapbox compatible styles

### **Layer Management:**

**Reorder Layers:**
- Drag layers in panel to change rendering order
- Higher layers render on top of lower layers

**Layer Settings:**
- Click **⚙️ Settings** for each layer
- Adjust opacity, style, and filter settings
- Configure popup templates for feature inspection

*🖼️ Screenshot needed: Layer management panel with reordering and settings*

---

## **Step 4: Interactive Data Exploration**

### **Feature Inspection**

Click map features to view detailed information:

1. Enable **Info Tool** in map toolbar
2. Click any feature on the map
3. Feature popup displays attribute data
4. Navigate between overlapping features

*🖼️ Screenshot needed: Feature popup showing attribute table*

### **Popup Configuration:**

**Default Popup:**
- Shows all feature attributes in table format
- Displays geometry type and coordinate system
- Includes links to raw data (GeoJSON, etc.)

**Custom Popup Templates:**
- Configure which attributes to display
- Format attribute values (dates, numbers, etc.)
- Add custom HTML formatting
- Include links and images

### **Attribute Search and Filtering:**

**Search Features:**
1. Open **🔍 Search** panel
2. Enter attribute values or expressions
3. Matching features are highlighted on map
4. Use advanced filters for complex queries

*🖼️ Screenshot needed: Search panel with feature filtering*

**Filter Examples:**
- **Text Search**: `name LIKE 'Main%'` - Find features with names starting with "Main"
- **Numeric Range**: `population BETWEEN 1000 AND 5000`
- **Date Range**: `last_updated > '2024-01-01'`
- **Spatial**: Features within current map extent

---

## **Step 5: Basemap and Background Options**

### **Basemap Selection**

Choose appropriate background maps for your data:

1. Click **🗺️ Basemap** selector
2. Choose from available basemap options
3. Basemap loads as bottom layer

*🖼️ Screenshot needed: Basemap selector with options*

**Available Basemaps:**
- **OpenStreetMap**: Open source street map
- **Satellite**: Aerial imagery background
- **Terrain**: Topographic relief maps
- **Dark**: Dark theme for bright data overlays
- **Light**: Minimal light background
- **None**: No background (transparent)

### **Custom Basemaps:**

Add external tile services as basemaps:

**Configuration:**
- **URL Template**: Tile URL pattern (e.g., `https://tiles.example.com/{z}/{x}/{y}.png`)
- **Attribution**: Required attribution text
- **Max Zoom**: Maximum supported zoom level
- **Format**: PNG, JPEG, or vector tiles (MVT)

---

## **Step 6: Sharing and Export**

### **Share Map Views**

Generate shareable links to specific map states:

1. Configure desired map view (extent, layers, styles)
2. Click **🔗 Share** button
3. Copy generated permalink
4. Share with stakeholders or embed in documents

*🖼️ Screenshot needed: Share dialog with permalink and embed options*

**Share Options:**
- **Permalink**: Direct link to current map view
- **Embed Code**: HTML iframe for web pages
- **Screenshot**: PNG export of current view
- **Print View**: Printer-friendly map layout

### **Export Capabilities:**

**Static Image Export:**
- **PNG**: High-quality raster image
- **SVG**: Scalable vector graphics
- **PDF**: Print-ready format with legend

**Data Export:**
- **Current View**: Export features visible in current extent
- **Selected Features**: Export only selected/filtered features
- **Complete Layer**: Export entire layer dataset
- **Format Options**: GeoJSON, KML, Shapefile, CSV

*🖼️ Screenshot needed: Export dialog with format and extent options*

---

## **Step 7: Advanced Preview Features**

### **Split Screen Comparison**

Compare different map configurations side-by-side:

1. Enable **Split Screen** mode
2. Configure different layers/styles for each pane
3. Synchronized navigation between panes

*🖼️ Screenshot needed: Split screen view comparing two layer configurations*

### **Time Series Visualization**

For layers with temporal data:

**Time Controls:**
- **Time Slider**: Scrub through temporal data
- **Play/Pause**: Animate temporal changes
- **Speed Control**: Adjust animation speed
- **Date Range**: Filter to specific time periods

*🖼️ Screenshot needed: Time series controls with temporal data*

### **3D Visualization**

View data in 3D perspective:

1. Enable **3D Mode** in view controls
2. Adjust camera angle and perspective
3. Configure extrusion heights based on data attributes

**3D Features:**
- **Building Heights**: Extrude polygons based on attribute values
- **Terrain**: Integrate elevation data for 3D landscape
- **Camera Controls**: Tilt, rotate, and elevation adjustment

*🖼️ Screenshot needed: 3D map view with extruded buildings*

---

## **Step 8: Performance and Optimization**

### **Performance Monitoring**

Monitor map performance during preview:

1. Open browser developer tools
2. Monitor network requests for tile loading
3. Check rendering performance metrics
4. Identify slow-loading layers or styles

**Performance Indicators:**
- **Tile Load Time**: Speed of vector/raster tile requests
- **Rendering FPS**: Frames per second during interaction
- **Memory Usage**: Client-side memory consumption
- **Feature Count**: Number of features rendered per view

### **Optimization Tips:**

**Layer Optimization:**
- Reduce number of visible layers simultaneously
- Use appropriate zoom ranges for detailed layers
- Simplify complex geometries at lower zoom levels

**Style Optimization:**
- Minimize complex expressions and filters
- Use data-driven styling efficiently
- Cache rendered tiles when possible

*🖼️ Screenshot needed: Performance monitoring panel with metrics*

---

## 🔧 **Troubleshooting Preview**

### **Common Preview Issues**

**"Map not loading"**
- Check browser WebGL support: Visit [WebGL Test](https://webglreport.com/)
- Verify published layers have vector tile support enabled
- Clear browser cache and reload page
- Check browser console for JavaScript errors

**"Layers not appearing"**
- Verify layer is enabled and published correctly
- Check zoom level is within layer's zoom range
- Confirm layer has data in current map extent
- Test layer endpoints directly in browser

**"Style not rendering correctly"**
- Verify style is saved and applied to layer
- Check style expressions reference correct attribute names
- Test with default styling to isolate style issues
- Use browser dev tools to debug style errors

**"Poor performance"**
- Reduce number of simultaneously visible layers
- Simplify complex styles and expressions
- Check tile size and feature density
- Monitor network and rendering performance

### **Browser Compatibility**

**Supported Browsers:**
- **Chrome**: Full feature support
- **Firefox**: Full feature support
- **Safari**: Most features, some WebGL limitations
- **Edge**: Full feature support

**Mobile Support:**
- **iOS Safari**: Touch interaction supported
- **Android Chrome**: Full mobile functionality
- **Responsive Design**: Adapts to mobile screen sizes

---

## **Integration with GIS Desktop Software**

### **Connecting External GIS Clients**

Use preview to verify data before connecting desktop software:

**ArcGIS Pro Connection:**
```
https://your-honua.example.com/api/services/{layer-name}/FeatureServer/0
```

**QGIS Vector Tile Connection:**
```
https://your-honua.example.com/api/tiles/{layer-name}/{z}/{x}/{y}.mvt
```

**Verify Connection:**
1. Test layer in Honua preview first
2. Copy appropriate service URL
3. Add as layer in desktop GIS
4. Compare rendering with Honua preview

*🖼️ Screenshot needed: Side-by-side comparison of Honua preview and QGIS*

---

## ➡️ **Next Steps**

After previewing your data:

1. **[Style Refinement](styles-guide.md)** - Improve visual styling based on preview
2. **[Layer Configuration](layers-guide.md)** - Adjust layer settings for optimal performance
3. **[API Integration](../API_EXAMPLES.md)** - Implement previewed maps in applications

---

## 🔗 **Related Documentation**

- [Map Styles](styles-guide.md) - Creating custom visualizations
- [Layer Publishing](layers-guide.md) - Publishing data for preview
- [Vector Tiles](../STANDARDS_APIS.md#vector-tiles-mvt) - Understanding tile services
- [Geospatial API Examples](../API_EXAMPLES.md) - Using Honua maps in applications

---
*Map preview transforms raw spatial data into insightful visualizations - use it to explore, validate, and share your geospatial information effectively.*