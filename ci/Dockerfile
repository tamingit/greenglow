# The CI image: .NET 10, Node 24 and Chromium, to run every check and the browser tests.
#   docker build -f ci/Dockerfile -t greenglow-ci .
#   docker run --rm greenglow-ci              (npm run ci)
# Pass E2E_IBMI_TARGET, E2E_IBMI_USER, E2E_IBMI_PASSWORD (and E2E_IBMI_NODE,
# E2E_IBMI_CA_PEM for tls://) with -e NAME to also test a real IBM i test system.

FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG NODE_VERSION=24.21.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends xz-utils \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.xz" \
    | tar -xJ -C /usr/local --strip-components=1
ENV CI=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    E2E_LOCAL_BROWSER=1
WORKDIR /src

# Dependencies first, so they stay cached while the source changes.
COPY package.json package-lock.json .npmrc ./
COPY packages/web/package.json packages/web/
COPY tools/package.json tools/
RUN npm ci --ignore-scripts && npx playwright install --with-deps chromium
COPY server/ server/
RUN dotnet restore server/GreenGlow.slnx

COPY . .
CMD ["npm", "run", "ci"]
