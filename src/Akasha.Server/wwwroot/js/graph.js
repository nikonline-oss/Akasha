// Akasha graph — JS interop for Cytoscape.js
window.akashaGraph = {
  init: function (containerId, elements, dotnetRef) {
    var container = document.getElementById(containerId);
    if (!container) return;

    var cy = cytoscape({
      container: container,
      elements: elements,
      style: [
        {
          selector: 'node',
          style: {
            'background-color': 'data(color)',
            'label': 'data(label)',
            'width': 22,
            'height': 22,
            'font-size': 10,
            'text-valign': 'bottom',
            'text-margin-y': 5,
            'text-wrap': 'ellipsis',
            'text-max-width': '80px',
            'color': '#333',
            'border-width': 1.5,
            'border-color': '#C9A227'
          }
        },
        {
          selector: 'edge',
          style: {
            'width': 1.5,
            'line-color': '#9a7fd0',
            'target-arrow-shape': 'triangle',
            'target-arrow-color': '#9a7fd0',
            'curve-style': 'bezier',
            'label': 'data(relation)',
            'font-size': 8,
            'text-rotation': 'autorotate',
            'color': '#8a8a8a'
          }
        }
      ],
      layout: {
        name: 'cose',
        animate: false,
        padding: 30,
        nodeRepulsion: 8000,
        idealEdgeLength: 90
      }
    });

    cy.on('tap', 'node', function (evt) {
      var id = evt.target.id();
      if (dotnetRef) dotnetRef.invokeMethodAsync('OpenEntry', id);
    });

    window.__akashaCy = cy;
  }
};
