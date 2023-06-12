CONTAINER_REGISTRY_NAME=proventaskregistry
RESOURCE_GROUP=proventask

CONTAINER_REGISTRY_LOGIN_SERVER="$CONTAINER_REGISTRY_NAME.azurecr.io"
echo "pushing latest image of $CONTAINER_REGISTRY_LOGIN_SERVER"

# see https://learn.microsoft.com/en-us/azure/container-instances/container-instances-using-azure-container-registry
# and https://learn.microsoft.com/en-us/azure/container-registry/container-registry-authentication?tabs=azure-cli
TOKEN=$(az acr login --name $CONTAINER_REGISTRY_NAME --expose-token --output tsv --query accessToken)
docker login $CONTAINER_REGISTRY_LOGIN_SERVER --username 00000000-0000-0000-0000-000000000000 --password-stdin <<< $TOKEN

IMAGE_TAG=proventaskregistry.azurecr.io/proventask-proxy-server:`date '+%Y%m%d%H%M%S'`
echo 'Adding tag to image with "latest" tag: ' $IMAGE_TAG
docker image tag proventaskregistry.azurecr.io/proventask-proxy-server:latest $IMAGE_TAG
docker push $IMAGE_TAG

echo 'Creating azure container instance'
az container create --resource-group $RESOURCE_GROUP --name proxy-server --image $IMAGE_TAG --ports 4000 443 --registry-login-server $CONTAINER_REGISTRY_LOGIN_SERVER \
  --registry-username 00000000-0000-0000-0000-000000000000 --registry-password $TOKEN --dns-name-label proventask-proxy-server --query ipAddress --output tsv

ACI_IP=$(az container show --name proxy-server --resource-group $RESOURCE_GROUP  --query ipAddress.ip --output tsv)

echo "Container instance ip: $ACI_IP"
az network dns record-set a update -g $RESOURCE_GROUP -n dev -z proventask.com --set "aRecords[0]ipv4Address=$ACI_IP"

# az container logs --resource-group $RESOURCE_GROUP --name proxy-server
# az container attach --resource-group $RESOURCE_GROUP --name proxy-server
# az container delete --resource-group $RESOURCE_GROUP --name proxy-server