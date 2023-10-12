@echo off
REM deploys all Kubernetes services to their staging environment

set namespace=phobos-web
set location=%~dp0/environment

REM AppInsights key is equal to the first commandline argument
set appInsightsConnectionString=%~1
Echo the string passed to this script is %appInsightsConnectionString%

if "%~1"=="" (
	REM No App Insights key provided.
	echo No Application Insights connection string has been provided. Can't complete deployment.
	echo Run the script using the following syntax:
    echo 'deployAll.cmd [appInsights connection string]'
    exit 1
) 


echo "Deploying K8s resources from [%location%] into namespace [%namespace%]"

echo "Creating Namespaces..."
kubectl apply -f "%~dp0/namespace.yaml"

echo "Using namespace [%namespace%] going forward..."

echo "Creating secret for Application Insights API key"
kubectl create secret generic appinsights -n %namespace% ^
--from-literal=APPLICATIONINSIGHTS_CONNECTION_STRING=%appInsightsConnectionString%

echo "Creating configurations from YAML files in [%location%/configs]"
for %%f in (%location%/configs/*.yaml) do (
    echo "Deploying %%~nxf"
    kubectl apply -f "%location%/configs/%%~nxf" -n "%namespace%"
)

echo "Creating environment-specific services from YAML files in [%location%]"
for %%f in (%location%/*.yaml) do (
    echo "Deploying %%~nxf"
    kubectl apply -f "%location%/%%~nxf" -n "%namespace%"
)

echo "Creating all services..."
for %%f in (%~dp0/services/*.yaml) do (
    echo "Deploying %%~nxf"
    kubectl apply -f "%~dp0/services/%%~nxf" -n "%namespace%"
)

echo "All services started... Printing K8s output.."
kubectl get all -n "%namespace%"