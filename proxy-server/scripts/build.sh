scriptFullPath=`realpath $0`
scriptDirectory=`dirname $scriptFullPath`
cd $scriptDirectory

echo 'Changing one directory up to docker file'
cd ../
echo 'Current directory: ' `pwd`

IMAGE_TAG=proventaskregistry.azurecr.io/proventask-proxy-server:latest
echo 'Building image with tag: ' $IMAGE_TAG
docker build -t $IMAGE_TAG -f Dockerfile ../
