# Map Styles Guide

Honua Server includes an integrated Maputnik style editor for creating custom map visualizations. Create beautiful, interactive styles for your vector tile services using the MapLibre GL style specification.

## 📋 **Prerequisites**

- Published layers with vector tile support enabled
- Basic understanding of map styling concepts
- Browser with WebGL support for style preview

## 🎯 **Styling Overview**

### **Style Components:**
- **Layers**: Visual representation rules for data
- **Sources**: Data sources (your published layers)
- **Sprites**: Icon and symbol libraries
- **Glyphs**: Font files for text labels

### **Layer Types:**
- **Fill**: Polygon areas with colors and patterns
- **Line**: Linear features like roads and borders
- **Symbol**: Points, icons, and text labels
- **Circle**: Simple point markers
- **Background**: Map background colors

---

## **Step 1: Access Style Editor**

Open the integrated Maputnik editor:

1. Navigate to Admin UI at `/admin`
2. Click **🎨 Styles** in the sidebar
3. Maputnik editor loads with style management interface

*🖼️ Screenshot needed: Styles page with Maputnik integration*

---

## **Step 2: Style Management**

Manage your collection of map styles:

### **Create New Style**

1. Click **➕ New Style** button
2. Choose starting template:
   - **Blank**: Empty style for custom design
   - **Basic**: Simple style with common layer types
   - **Satellite**: Optimized for aerial imagery backgrounds
   - **Dark**: Dark theme template

*🖼️ Screenshot needed: New style creation dialog with templates*

### **Style Library**

View and manage existing styles:
- **Style Name**: Display name for the style
- **Description**: Purpose and usage notes
- **Last Modified**: Recent edit timestamp
- **Actions**: Edit, Duplicate, Delete, Export

*🖼️ Screenshot needed: Style library showing multiple custom styles*

---

## **Step 3: Maputnik Style Editor**

Use the integrated Maputnik editor for visual style design:

### **Editor Layout:**

*🖼️ Screenshot needed: Full Maputnik interface within Honua Admin UI*

**Left Panel: Layers**
- Layer list with drag-and-drop ordering
- Layer visibility toggles
- Add/remove/duplicate layers

**Center: Map Preview**
- Interactive style preview
- Real-time updates as you edit
- Zoom controls and navigation

**Right Panel: Properties**
- Layer properties editor
- Data source configuration
- Filter and expression editors

### **Adding Data Sources**

Connect your published layers to the style:

1. Click **📊 Sources** tab in editor
2. Click **➕ Add Source**
3. Configure vector tile source:
   - **Source ID**: Unique identifier
   - **Type**: Vector (for your published layers)
   - **URL**: Your layer's tile endpoint
   - **Layers**: Available layer names from source

*🖼️ Screenshot needed: Add source dialog with Honua layer configuration*

### **Source URL Format:**
```
{honua-base-url}/api/tiles/{layer-name}/{z}/{x}/{y}.mvt
```

Example:
```
https://your-honua.example.com/api/tiles/parcels/{z}/{x}/{y}.mvt
```

---

## **Step 4: Creating Layers**

Add visual layers to render your data:

### **Add New Layer**

1. Click **➕ Add Layer** in layers panel
2. Select layer type (Fill, Line, Symbol, Circle)
3. Choose data source and source layer
4. Configure basic properties

*🖼️ Screenshot needed: Add layer dialog with type selection*

### **Layer Properties**

Configure how data appears on the map:

**Basic Properties:**
- **ID**: Unique layer identifier
- **Type**: Visual rendering method
- **Source**: Data source to render
- **Source Layer**: Specific layer from source
- **Min/Max Zoom**: Visibility zoom range

**Style Properties:**
- **Color**: Fill color or stroke color
- **Opacity**: Transparency level
- **Width**: Line width (for line layers)
- **Radius**: Circle size (for circle layers)

*🖼️ Screenshot needed: Layer properties panel with color picker*

### **Data-Driven Styling**

Create dynamic styles based on data attributes:

**Expression-Based Styling:**
```json
{
  "fill-color": [
    "case",
    ["<", ["get", "population"], 10000], "#ffffcc",
    ["<", ["get", "population"], 50000], "#a1dab4",
    "#41b6c4"
  ]
}
```

**Categorical Styling:**
```json
{
  "circle-color": [
    "match",
    ["get", "category"],
    "residential", "#ff9999",
    "commercial", "#9999ff",
    "industrial", "#99ff99",
    "#cccccc"
  ]
}
```

*🖼️ Screenshot needed: Expression editor with data-driven style example*

---

## **Step 5: Advanced Styling**

### **Filters and Conditions**

Show or hide features based on data attributes:

**Simple Filter:**
```json
["==", ["get", "type"], "highway"]
```

**Complex Filter:**
```json
[
  "all",
  [">=", ["get", "population"], 1000],
  ["<", ["get", "area"], 10]
]
```

*🖼️ Screenshot needed: Filter editor with condition builder*

### **Symbol Layers and Labels**

Add icons and text labels to your maps:

**Text Labels:**
- **Text Field**: `["get", "name"]` to show feature names
- **Font**: Choose from available font families
- **Size**: Dynamic sizing based on zoom level
- **Color**: Text and halo colors

**Icons:**
- **Icon Image**: Reference to sprite library
- **Icon Size**: Scale factor for icons
- **Icon Rotation**: Rotate icons based on data

*🖼️ Screenshot needed: Symbol layer configuration with text and icon options*

### **3D Effects**

Add depth and visual interest:

**Extrusions:**
```json
{
  "fill-extrusion-height": ["*", ["get", "floors"], 3],
  "fill-extrusion-color": "#aaa"
}
```

**Shadows and Lighting:**
- Adjustable light angle and intensity
- Realistic building shadows
- Terrain elevation effects

---

## **Step 6: Testing and Preview**

### **Live Preview**

Test your style with real data:

1. Map updates automatically as you edit
2. Test different zoom levels
3. Verify performance with complex styles
4. Check rendering across different data densities

*🖼️ Screenshot needed: Style preview at different zoom levels*

### **Style Validation**

Maputnik provides automatic validation:

- **Syntax Errors**: Invalid JSON or expressions
- **Missing Sources**: Referenced data sources that don't exist
- **Performance Warnings**: Styles that may render slowly
- **Accessibility**: Color contrast and readability issues

*🖼️ Screenshot needed: Validation panel showing errors and warnings*

---

## **Step 7: Publishing Styles**

### **Save and Deploy**

Make styles available for use:

1. Click **💾 Save** to store style changes
2. Style becomes available for layer configuration
3. Export style JSON for external use

### **Style Export Formats**

Export styles for different platforms:

- **MapLibre GL JS**: Web mapping libraries
- **Mapbox GL JS**: Compatible web format
- **QGIS**: For desktop GIS integration
- **Raw JSON**: Programmatic access

*🖼️ Screenshot needed: Export dialog with format options*

### **Apply Styles to Layers**

Connect styles to published layers:

1. Navigate to **📄 Layers** page
2. Edit layer configuration
3. Select custom style from dropdown
4. Save layer settings

---

## **Style Templates and Examples**

### **Common Style Patterns**

**Population Density Choropleth:**
```json
{
  "type": "fill",
  "source": "census-data",
  "paint": {
    "fill-color": [
      "interpolate",
      ["linear"],
      ["get", "density"],
      0, "#f7f7f7",
      100, "#d9d9d9",
      500, "#969696",
      1000, "#525252"
    ],
    "fill-opacity": 0.8
  }
}
```

**Road Classification:**
```json
{
  "type": "line",
  "source": "roads",
  "paint": {
    "line-color": [
      "match",
      ["get", "highway"],
      "motorway", "#e892a2",
      "trunk", "#f9ca9b",
      "primary", "#f7f496",
      "secondary", "#96f7f4",
      "#cccccc"
    ],
    "line-width": [
      "interpolate",
      ["exponential", 1.5],
      ["zoom"],
      5, 0.5,
      18, 8
    ]
  }
}
```

*🖼️ Screenshot needed: Example styles applied to different data types*

---

## 🔧 **Troubleshooting Styles**

### **Common Style Issues**

**"Layer not visible"**
- Check zoom level ranges (min-zoom, max-zoom)
- Verify data source is loading correctly
- Ensure layer order (later layers render on top)
- Check filter expressions aren't excluding all data

**"Performance is slow"**
- Simplify complex expressions
- Reduce number of layers
- Use appropriate zoom ranges
- Consider data simplification

**"Colors look wrong"**
- Verify data attribute names match expressions
- Check data types (numeric vs. string)
- Test expressions with sample data
- Use browser developer tools for debugging

**"Fonts not loading"**
- Verify glyph URL is accessible
- Check font names match available fonts
- Use fallback fonts for compatibility

### **Performance Best Practices**

**Optimize Layer Count:**
- Combine similar layers when possible
- Use data-driven styling instead of multiple layers
- Remove unused layers from style

**Efficient Expressions:**
- Cache complex calculations
- Use simpler expressions at lower zoom levels
- Profile performance with browser dev tools

**Data Optimization:**
- Simplify geometries for tile generation
- Use appropriate detail levels for zoom ranges
- Consider data generalization strategies

---

## **Integration with External Tools**

### **QGIS Integration**

Export styles for use in QGIS:

1. Export style as QGIS-compatible format
2. Import style in QGIS project
3. Connect to Honua vector tile service
4. Apply exported style to layer

### **Web Application Integration**

Use styles in web maps:

```javascript
// MapLibre GL JS
const map = new maplibregl.Map({
  container: 'map',
  style: 'https://your-honua.example.com/api/styles/your-style'
});

// Add Honua layer with custom style
map.on('load', () => {
  map.addSource('honua-data', {
    type: 'vector',
    url: 'https://your-honua.example.com/api/tiles/your-layer'
  });

  // Style layers are already included in the style JSON
});
```

*🖼️ Screenshot needed: Web map using Honua styles and data*

---

## ➡️ **Next Steps**

After creating map styles:

1. **[Preview Your Maps](preview-guide.md)** - See styled data on interactive maps
2. **[Layer Management](layers-guide.md)** - Apply styles to published layers
3. **[API Integration](../API_EXAMPLES.md)** - Use styles in web applications

---

## 🔗 **Related Documentation**

- [Layer Publishing](layers-guide.md) - Publishing data for styling
- [Map Preview](preview-guide.md) - Testing styled maps
- [Vector Tiles](../STANDARDS_APIS.md#vector-tiles-mvt) - Technical details
- [MapLibre Style Spec](https://maplibre.org/maplibre-style-spec/) - Complete style reference

---
*Beautiful maps start with thoughtful styling - create visualizations that effectively communicate your spatial data story.*