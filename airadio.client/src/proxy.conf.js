const PROXY_CONFIG = [
  {
    context: [
      "/api",
    ],
    target: "https://localhost:7072",
    secure: false
  }
];

module.exports = PROXY_CONFIG;
